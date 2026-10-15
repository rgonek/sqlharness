# Plan 074: Session search ignores case beyond ASCII

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat a99ac3f..HEAD -- src/SqlHarness.Dashboard/JournalReader.cs tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs README.md`. Line numbers below are from `a99ac3f`. If `Sessions(SessionQuery)` changed, keep the same predicate shape; that is adaptation, not a STOP.

**Goal:** The dashboard's session search (`GET /api/sessions?q=`) ignores letter case for every language, so `łódź` finds `ŁÓDŹ` and `zażółć` finds `ZAŻÓŁĆ`, not only ASCII letters.

**Architecture:** SQLite's built-in `LIKE` folds ASCII letters only. `JournalReader` registers a deterministic SQLite function `sqlh_fold` on its read connection. The function normalizes text to Unicode NFC and lower-cases it with the invariant culture. The search predicate compares `sqlh_fold(column) LIKE $q`, where `$q` is built from the query folded the same way in C#. Literal matching of `%`, `_` and `\` stays as it is: the query is folded first, then escaped.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite (`SqliteConnection.CreateFunction`), xUnit.

**Spec:** none. Plan 069's spec (`docs/superpowers/specs/2026-10-14-dashboard-session-curation-design.md`, "case-insensitive substring") stated the intent; Plan 069 shipped it for ASCII only. The operator asked for this plan on 2026-10-15. This plan is the design record.

## Status

- DONE. Implementation commit `de721be` on branch `feat/plan-074-session-search-case-folding`. Windows gate exit 0 on `de721be`; literal pass line `verify: OK  stages=6  3m01s`. Linux gate waived by docs/agents/verification.md; not run.
- Priority P3. Effort S. Risk LOW: read path only, no schema change, no API change.

## Global Constraints

- Case-insensitive, accent-sensitive: `łódź` matches `ŁÓDŹ`; `lodz` does NOT match `Łódź`. Accent-insensitive search is out of scope.
- Folding is `value.Normalize(NormalizationForm.FormC).ToLowerInvariant()`, applied identically to the query and to every searched column. It never depends on the process culture.
- Searched columns stay exactly: `sessions.session_key`, `sessions.cwd`, `sessions.client_title`, `session_annotations.title`, `session_annotations.note`, and `agent_conversations.title`. The existing conversation-title arm (the `operation_agent_links` / `agent_conversations` EXISTS already in `Sessions`) stays and uses the same fold.
- `%`, `_` and `\` in `q` stay literal (existing tests `Search_matches_title_note_cwd_key_and_client_title_literally` and `Search_treats_backslash_and_underscore_literally_and_matches_the_client_title` pass unchanged).
- `q` length limit stays 200 characters, checked on the raw query as today.
- The dashboard opens the journal read-only; registering a function writes nothing.
- Do not auto-format files; touch only lines the change needs.
- Done means `pwsh ./scripts/verify.ps1` then `pwsh ./scripts/verify-linux.ps1`, one at a time, both green (the operator may waive Linux).

## Review Focus

1. A searched column that is NULL (no annotation, no client title) must not throw inside `sqlh_fold` — Task 1 test (`Keys("cli:")` returns sessions without annotations).
2. A query typed in decomposed form (NFD, e.g. `o` + U+0301) matches a title stored composed (NFC) — Task 1 test.
3. A process running under a Turkish culture (`tr-TR`) still folds `I` to `i` — Task 1 test.
4. The function must exist whenever the statement is prepared, also when `q` is absent: SQLite resolves functions at prepare time, so `$q IS NULL OR …` does not protect an unregistered name — Task 1 registers unconditionally; existing unfiltered tests cover it.
5. Accented letters are not stripped: `lodz` finds nothing for `Łódź` — Task 1 test.

## File map

| File | Responsibility |
|---|---|
| `src/SqlHarness.Dashboard/JournalReader.cs` | `FoldForSearch`, function registration, folded predicate |
| `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs` | folding tests |
| `README.md` | one clause in the dashboard paragraph |
| `plans/README.md`, `plans/history.md` | plan rows |

---

### Task 1: Fold case beyond ASCII in session search

**Files:**
- Modify: `src/SqlHarness.Dashboard/JournalReader.cs` (`Sessions` at ~116-152, `EscapeLike` at ~782)
- Modify: `README.md` (~514, Sessions page sentence)
- Test: `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs` (next to `Search_treats_backslash_and_underscore_literally_and_matches_the_client_title`)

**Interfaces:**
- Produces: `internal static string? JournalReader.FoldForSearch(string? value)`; SQLite function `sqlh_fold(text) → text` registered on the connection used by `JournalReader.Sessions`.

- [ ] **Step 1: Write the failing tests** (append to `JournalReaderTests`; add `using System.Globalization;` and `using System.Text;` if missing):

```csharp
[Fact]
public void Search_ignores_case_beyond_ascii_but_keeps_accents()
{
    using var home = new TempHome();
    var seed = new JournalSeed(home.DatabasePath);
    seed.Operation(JournalSeed.Session("cli:city"));
    seed.Operation(JournalSeed.Session("cli:turtle") with { Cwd = "/praca/ŻÓŁW" });
    seed.Operation(JournalSeed.Session("cli:plain"));
    JournalSeed.Annotate(home.DatabasePath, "cli:city", title: "Raport ŁÓDŹ", note: "Zażółć gęślą jaźń");
    var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

    string[] Keys(string q) => reader.Sessions(new SessionQuery(Search: q)).Items.Select(s => s.SessionKey).Order().ToArray();
    Assert.Equal(["cli:city"], Keys("łódź"));
    Assert.Equal(["cli:city"], Keys("ZAŻÓŁĆ GĘŚLĄ"));
    Assert.Equal(["cli:turtle"], Keys("żółw"));
    Assert.Equal(["cli:city"], Keys("łódź".Normalize(NormalizationForm.FormD)));
    Assert.Empty(Keys("lodz"));
    Assert.Equal(["cli:city", "cli:plain", "cli:turtle"], Keys("CLI:"));
}

[Fact]
public void Search_folding_does_not_depend_on_the_process_culture()
{
    using var home = new TempHome();
    var seed = new JournalSeed(home.DatabasePath);
    seed.Operation(JournalSeed.Session("cli:tr"));
    JournalSeed.Annotate(home.DatabasePath, "cli:tr", title: "INFO report");
    var reader = new JournalReader(home.DatabasePath, new FakeProcesses());
    var previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        Assert.Equal(["cli:tr"], reader.Sessions(new SessionQuery(Search: "info")).Items.Select(s => s.SessionKey).ToArray());
    }
    finally
    {
        CultureInfo.CurrentCulture = previous;
    }
}
```

`JournalSeed.Session(...) with { Cwd = ... }` is the pattern the backslash test already uses.

- [ ] **Step 2: Run and confirm RED**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Search_ignores_case_beyond_ascii|FullyQualifiedName~Search_folding_does_not_depend"`
Expected: `Search_ignores_case_beyond_ascii_but_keeps_accents` FAILS (`łódź` finds nothing, because SQLite LIKE does not fold `Ł`). The culture test may already pass (SQLite folds ASCII `I`); it pins the behavior for the new code.

- [ ] **Step 3: Implement.** In `JournalReader.cs`, next to `EscapeLike`:

```csharp
    /// <summary>
    /// Search folding shared by the query and every searched column: NFC, then invariant
    /// lower case, so "łódź" finds "ŁÓDŹ". SQLite's own LIKE folds ASCII letters only.
    /// Accents still have to match.
    /// </summary>
    internal static string? FoldForSearch(string? value) =>
        value?.Normalize(NormalizationForm.FormC).ToLowerInvariant();
```

In `Sessions(SessionQuery query)`, right after the `connection is null` check, register the function **unconditionally** (SQLite resolves function names when the statement is prepared, even inside `$q IS NULL OR …`):

```csharp
        connection.CreateFunction<string?, string?>("sqlh_fold", FoldForSearch, isDeterministic: true);
```

Change the `$q` parameter to fold before escaping:

```csharp
            ("$q", query.Search is null ? null : "%" + EscapeLike(FoldForSearch(query.Search)!) + "%"),
```

Change the search predicate to fold each column:

```sql
              AND ($q IS NULL
                   OR sqlh_fold(s.session_key) LIKE $q ESCAPE '\' OR sqlh_fold(s.cwd) LIKE $q ESCAPE '\'
                   OR sqlh_fold(s.client_title) LIKE $q ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM session_annotations a WHERE a.session_id = s.id
                              AND (sqlh_fold(a.title) LIKE $q ESCAPE '\' OR sqlh_fold(a.note) LIKE $q ESCAPE '\')))
```

`System.Text` is already imported (`NormalizationForm`). If Microsoft.Data.Sqlite throws on a NULL argument for `string?` (Review Focus 1 — the `Keys("CLI:")` assertion covers rows with NULL `client_title` and no annotation), register `CreateFunction<object?, string?>("sqlh_fold", value => FoldForSearch(value as string), isDeterministic: true)` instead and say so in the report.

- [ ] **Step 4: Run and confirm GREEN**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalReaderTests|FullyQualifiedName~DashboardServerTests|FullyQualifiedName~LiveFeedTests"`
Expected: PASS, including both existing literal-search tests unchanged.

- [ ] **Step 5: README.** In the Sessions page sentence (`README.md` ~514), change "The Sessions page searches title, note, directory, session key and client title," to "The Sessions page searches title, note, directory, session key and client title, ignoring letter case in any language (accents must match),". Touch nothing else.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Dashboard/JournalReader.cs tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs README.md
git commit -m "feat(dashboard): session search ignores case beyond ASCII"
```

---

### Task 2: Gates and plan rows

**Files:**
- Modify: `plans/README.md`, `plans/history.md`, this plan's `## Status`

- [ ] **Step 1: Gates.** Foreground, one at a time: `pwsh ./scripts/verify.ps1`; after it finishes, `pwsh ./scripts/verify-linux.ps1` (or record the operator's waiver). Record exit codes and the literal pass line of each; search each log case-sensitively for `FAILED`. A partial log is not a pass.

- [ ] **Step 2: Plan rows.** Remove the 074 row from the queue in `plans/README.md`; in `plans/history.md` set the 074 row to DONE with the commit range and gate results; set this plan's Status to DONE.

- [ ] **Step 3: Commit**

```bash
git add plans/README.md plans/history.md plans/074-session-search-case-folding.md
git commit -m "docs: mark plan 074 done"
```

Integration into `main` and any push only on a separate operator instruction.
