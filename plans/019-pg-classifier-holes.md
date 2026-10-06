# Plan 019: The PostgreSQL classifier denies self-named temp CTE writes, SQL-string executors and the remaining deny-list gaps

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1 (security)
- **Effort**: S
- **Risk**: LOW (only adds denials)
- **Depends on**: plans/016-restore-green-ci.md
- **Category**: security
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

The PostgreSQL static classifier (`PostgresSafetyClassifier`) enforces the contract: no persistent writes without `--allow-mutation --confirm-database`, no persistent writes at all in compare setup, no dynamic SQL, and no visible `set_config`/`setval`/`nextval`/advisory locks/backend termination. Three gaps break it:

1. **Self-named temp + CTE write.** For `SELECT … INTO TEMP t` and `CREATE TEMP TABLE t AS …`, the classifier records `t` as a proven session temp **before** checking the same statement's data-modifying CTE targets. A CTE `INSERT INTO t …` is therefore judged session-local. PostgreSQL resolves that CTE target during parse analysis, before the new temp exists, so through `search_path` it hits an existing **persistent** `t`. Result: an unapproved persistent write, also in compare setup. Server acceptance of a data-modifying CTE under `SELECT INTO` / CTAS is believed but **not proven live**. The fix is safe either way.
2. **SQL-string executors.** `query_to_xml`, `query_to_xmlschema`, `query_to_xml_and_xmlschema`, `cursor_to_xml` and `ts_stat` take SQL **text** and execute it. String literals are opaque to the parser, so wrapping any denied call (e.g. `set_config('search_path', …)`, `setval`, `pg_advisory_lock`, `pg_terminate_backend`) in one of them gets around every name denial. Changing `search_path` also defeats the temp-name proof's documented assumption.
3. **Deny-list siblings.** `pg_sleep` is exact, so `pg_sleep_for`/`pg_sleep_until` pass. The `NOTIFY` statement is denied but `pg_notify(...)` passes. `pg_stat_file` (server file metadata) passes although the policy scopes server file reads. Admin/WAL functions with cluster-wide effects pass: `pg_reload_conf`, `pg_rotate_logfile`, `pg_switch_wal`, `pg_create_restore_point`, `pg_stat_reset*`, `pg_create_*_replication_slot`, `pg_drop_replication_slot`, `pg_logical_emit_message`.

The documented policy (functions with *invisible* effects are accepted; no READ ONLY transaction) is **not** changed here. Every item above is a *visible* name or a statement-shape bug.

## Current state

Files:
- `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs`: the classifier.
- `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`: tests.
- `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`: the policy matrix (written in Polish; keep additions in the same language and table style as lines 51-61).

Deny lists (`PostgresSafetyClassifier.cs:11-22` and `:903-918`):

```csharp
    private static readonly HashSet<string> DeniedExactFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "nextval",
        "setval",
        "pg_sleep",
        "pg_read_file",
        "pg_ls_dir",
        "lo_import",
        "set_config",
        "pg_cancel_backend",
        "pg_terminate_backend",
    };
...
            var functionName = name.Values[^1].Value;
            if (DeniedExactFunctions.Contains(functionName))
                return true;

            return functionName.StartsWith("dblink", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("pg_read_", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("pg_ls_", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("lo_", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("pg_advisory_", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("pg_try_advisory_", StringComparison.OrdinalIgnoreCase);
```

Matching is on the final name part, so `pg_catalog.x` and `"x"` are covered (existing tests at `PostgresSafetyTests.cs:148-150` prove this for `set_config`).

`CREATE TEMP TABLE … AS` path (`PostgresSafetyClassifier.cs:175-187`):

```csharp
                    var declared = create.Element.Name.Values;
                    knownTemps.Record(
                        key,
                        declared[^1],
                        survivesCommit: create.Element.OnCommit != OnCommit.Drop,
                        ifNotExists: create.Element.IfNotExists,
                        provesName: declared.Count == 1 ||
                            (declared.Count == 2 && IsPgTempAlias(declared[0])));
                    return FromWrites(effect.Targets, knownTemps, emptyIsSessionLocal: true);
```

`SELECT … INTO TEMP` path (`ClassifySelect`, `:354-375`):

```csharp
        var key = ObjectKey(into.Name);
        if (key is null)
            return StatementOutcome.Unsupported;
        knownTemps.Record(key, into.Name.Values[^1], survivesCommit: true, ifNotExists: false, provesName: true);
        if (FromWrites(effect.Targets, knownTemps).Kind == StatementKind.Mutation)
            return StatementOutcome.SessionLocalMutation;
        return StatementOutcome.SessionLocal;
```

In both, `Record` runs before `FromWrites(effect.Targets, …)`, so a CTE write to the same name sees the name as already proven.

Read `SessionTemps` (search `class SessionTemps` in the same file) before editing. You need a way to evaluate `FromWrites` against the temps **as they were before** this statement. Either check `effect.Targets` before calling `Record`, or snapshot or clone `SessionTemps`. Choose the smallest change that keeps `FromWrites`'s signature.

Test pattern (`PostgresSafetyTests.cs:96-110`, denials as `[Theory]` rows):

```csharp
    [InlineData("SELECT nextval('s')")]
    [InlineData("SELECT dblink('dbname=other','select 1')")]
    [InlineData("SELECT * FROM pg_ls_dir('.')")]
```

Find the method those rows belong to and add rows there or in a new sibling theory with identical assertions. There is an existing test at about `:558-572` that parses `WITH … (INSERT …) SELECT … INTO TEMP TABLE t` with a **different** CTE target (`public.items`). Use it as the template for the self-named case.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| PG classifier tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Postgres"` | all pass |
| Capabilities/validate | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Capabilities|FullyQualifiedName~Validate"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**:
- `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs`
- `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`
- `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`: rows for the new names only

**Out of scope**:
- Row-locking `SELECT … FOR UPDATE/SHARE`. This is a policy decision recorded in `plans/README.md`, not this plan.
- Auth method restriction and `standard_conforming_strings` (plan 031).
- Any READ ONLY transaction, role change or function-identity resolution. These are rejected by the existing policy.
- `Capabilities.cs` text, unless a capabilities test proves the denial list is enumerated there (grep `pg_advisory_` in `src/SqlHarness.Core/Capabilities.cs`; if it is listed, add the new prefixes in the same style and update the pinned test).

## Git workflow

- Branch `fix/plan-019-pg-classifier`. Commits: `test(019): ...` (RED), `fix(019): check CTE writes before recording the new temp`, `fix(019): deny SQL-string executors and deny-list siblings`, `docs(019): policy rows`. Do NOT push.

## Steps

### Step 1: RED tests

1. Self-named CTE writes, theory over `SqlUsage.Query` (no approval → expect `Allowed == false`, `Reason == MutationNotAllowed`) and `SqlUsage.CompareSetup` (expect `NonTemporaryWrite`):
   - `WITH w AS (INSERT INTO t (id) VALUES (1) RETURNING id) SELECT id INTO TEMP TABLE t FROM w`
   - `CREATE TEMP TABLE t AS WITH w AS (INSERT INTO t (id) VALUES (1) RETURNING id) SELECT id FROM w`

   If SqlParserCS cannot parse the CTAS form, keep the first row only and note it.
   With approval (`allowMutation: true, confirmDatabase: "db"`), the Query form must be allowed with `HasMutation == true`.
2. Regression: a CTE write to an **already proven earlier** temp stays session-local. Use `CREATE TEMP TABLE t (id int); WITH w AS (INSERT INTO t (id) VALUES (1) RETURNING id) SELECT id INTO TEMP TABLE u FROM w` → allowed without approval. If this is not allowed **today**, drop the row and record that.
3. Deny rows (both usages, `Reason == UnsupportedStatement`), each also with a `pg_catalog.` qualified variant where the function lives there:
   `SELECT query_to_xml('select 1', true, false, '')`, `SELECT query_to_xmlschema('select 1', true, false, '')`, `SELECT query_to_xml_and_xmlschema('select 1', true, false, '')`, `SELECT cursor_to_xml('c', 1, true, false, '')`, `SELECT * FROM ts_stat('select v from docs')`, `SELECT pg_sleep_for('1 second')`, `SELECT pg_sleep_until(now())`, `SELECT pg_notify('c', 'x')`, `SELECT * FROM pg_stat_file('postgresql.conf')`, `SELECT pg_reload_conf()`, `SELECT pg_rotate_logfile()`, `SELECT pg_switch_wal()`, `SELECT pg_create_restore_point('x')`, `SELECT pg_stat_reset()`, `SELECT pg_stat_reset_shared('bgwriter')`, `SELECT pg_create_physical_replication_slot('s')`, `SELECT pg_create_logical_replication_slot('s','pgoutput')`, `SELECT pg_drop_replication_slot('s')`, `SELECT pg_logical_emit_message(true, 'p', 'x')`.

**Verify**: build and run the Postgres tests. The rows in (1) and (3) FAIL; (2) passes or is dropped.

### Step 2: Check CTE writes against the pre-statement temp set

In both paths, evaluate `FromWrites(effect.Targets, …)` **before** `knownTemps.Record(...)`, keep the outcome, then record. For `ClassifySelect`:

```csharp
        // 019: the statement's own CTE writes resolve before its INTO relation exists.
        var writes = FromWrites(effect.Targets, knownTemps);
        knownTemps.Record(key, into.Name.Values[^1], survivesCommit: true, ifNotExists: false, provesName: true);
        if (writes.Kind == StatementKind.Mutation)
            return StatementOutcome.SessionLocalMutation;
        return StatementOutcome.SessionLocal;
```

Keep any other `writes.Kind` handling the existing code applies after `FromWrites` (read the whole method; if `FromWrites` can return `NonTemporaryWrite`/`Unsupported`, propagate them the same way the existing code does). Do the equivalent in the CREATE TEMP path, keeping `emptyIsSessionLocal: true`. Keep the original return mapping.

**Verify**: the Step 1 (1) rows pass; all Postgres tests pass.

### Step 3: Extend the deny list

- Add to `DeniedExactFunctions`: `query_to_xml`, `query_to_xmlschema`, `query_to_xml_and_xmlschema`, `cursor_to_xml`, `cursor_to_xmlschema`, `ts_stat`, `pg_notify`, `pg_stat_file`, `pg_reload_conf`, `pg_rotate_logfile`, `pg_switch_wal`, `pg_create_restore_point`, `pg_drop_replication_slot`, `pg_logical_emit_message`.
- Add prefixes in `IsDeniedObjectName`: `pg_sleep` (replaces the exact entry, so remove `"pg_sleep"` from the exact set), `pg_stat_reset`, `pg_create_` (covers the replication-slot creators), `pg_replication_origin_`.
- Add a comment `// 019: SQL-string executors run unclassified text; ...` above the new exact entries.

**Over-block check:** confirm no **allowed** existing test uses a function starting with `pg_create_`, `pg_stat_reset` or `pg_sleep`. Run the suite; a failure here is a STOP, not a test edit.

**Verify**: the Step 1 (3) rows pass; all Postgres and capabilities/validate tests pass.

### Step 4: Policy document

In `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`, add rows to the effects table (style of lines 51-54, in Polish):

- "Wykonanie SQL z tekstu" (`query_to_xml*`, `cursor_to_xml*`, `ts_stat`): denied by exact name, because the text argument is not classified.
- "Funkcje administracyjne i WAL": the list above.

Extend the closing sentence at line 61 to name the new exact names and prefixes. Do not alter any other row.

**Verify**: `git diff --stat` shows only in-scope files. `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

- New rows: 2 self-named CTE rows × 2 usages plus the approval assertion, 1 regression row, about 19 deny rows × 2 usages.
- Pattern: existing deny theory around `PostgresSafetyTests.cs:96-110`, and the CTE + `INTO TEMP` test around `:558-572`.
- Optional live evidence (only on the opt-in playground `scripts/setup-local-postgres.ps1`, never a user profile): confirm PostgreSQL accepts the self-named CTE + `SELECT INTO TEMP` and that the CTE write lands in the persistent table. Record the result in the index row.

## Done criteria

- [ ] `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Postgres"` passes with the new rows
- [ ] `grep -n '"query_to_xml"' src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs` → match
- [ ] `grep -n '"pg_sleep",' src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs` → no match (replaced by the prefix)
- [ ] `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`
- [ ] Only in-scope files changed
- [ ] `plans/README.md` row updated

## STOP conditions

- `SessionTemps.Record` has side effects that `FromWrites` depends on in a way the reorder breaks (an existing test fails after Step 2).
- Any existing **allowed** test is denied after Step 3.
- SqlParserCS cannot parse **either** self-named form. Report it; the risk then rests on the server parse alone and the plan's Step 2 is moot.

## Maintenance notes

- The deny list is name-based by design (policy §Classify). Every future PostgreSQL major adds functions; when bumping the supported PG majors (plan 037), review the release notes for functions that execute SQL text or change cluster state.
- Reviewers: verify the reorder in Step 2 does not change the outcome for a statement with **no** CTE writes (the bulk of existing tests cover this).
