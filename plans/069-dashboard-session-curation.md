# Plan 069: Dashboard session curation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 380307c..HEAD -- src/SqlHarness.Core/Journal src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard tests/SqlHarness.Tests/Journal README.md`. Plan 062 must be DONE before this plan starts (it changes the journal schema, `JournalReader`, `DashboardModels`, `SessionPage.tsx`). Plans 062/068 and others take journal schema versions. **This plan's code uses `Version9` / `CurrentVersion = 9`; if 9 is already taken when you start, use the next free number everywhere (`VersionN`, tests, the plan row) — that is adaptation, not a STOP.** Positional record constructors below list the fields that exist at `380307c`; if 062 added fields, keep them and append the new ones at the end.

**Goal:** The operator can title, annotate, star (pin) and archive agent sessions in the dashboard and search them.

**Architecture:** Operator-authored data lives in a new journal table `session_annotations` (same `activity.db`, separate from agent-recorded `sessions`). `JournalReader` exposes the annotation through correlated subqueries in `SessionColumns`, so every existing session query (list, detail, live feed) carries it. A small `SessionAnnotationWriter` in the dashboard is the only dashboard code that writes to the journal; it is reached through one new guarded `PATCH` endpoint. The UI shows starred sessions as a separate pinned list above the regular list (two filtered queries using the existing id cursor, so paging stays simple).

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, ASP.NET Core minimal APIs, xUnit; React 19, TypeScript, TanStack Query/Router, Vitest, Testing Library.

**Spec:** [2026-10-14-dashboard-session-curation-design.md](../docs/superpowers/specs/2026-10-14-dashboard-session-curation-design.md)

## Status

- DONE 2026-10-15; implementation `563e484`..`dd2b2de`, locally merged to main. Windows `verify: OK stages=6` on `dd2b2de`; Linux gate waived by the operator. Deviations from the plan text (operator-visible): `archived=any` for Live, Live fetches starred and unstarred lists, unfiltered list returns no cursor, session heading uses the display-name rule, 404 PATCH carries an `error` body.
- Priority P2. Effort M. Risk MEDIUM (journal migration; first dashboard write into the journal).

## Global Constraints

- Storage: table `session_annotations` in `activity.db`; never new columns on `sessions`; agents never write it.
- `title`: trimmed, 1..200 characters, no `\r`/`\n`; empty or whitespace stores `NULL`.
- `note`: plain text, multi-line, max 10 000 characters (`string.Length`); empty or whitespace stores `NULL`.
- `starred` 0/1; `archived_at` NULL = active, ISO-8601 UTC (`yyyy-MM-dd'T'HH:mm:ss.fff'Z'`) like other journal timestamps; archiving twice keeps the first time.
- Retention is unchanged in policy: deleting a session deletes its annotation; a star does not protect a session.
- Archive only affects the Sessions list. Live, Operations, Stats count archived sessions as before. Archiving keeps the star.
- Display name: `title ?? sessionKey` (Spec C later inserts the agent title between them; keep the rule in one UI helper).
- The dashboard never migrates the journal. The writer refuses (409) when `PRAGMA user_version != JournalSchema.CurrentVersion`.
- The write goes through `DashboardSecurity` write protection (custom header, JSON content type, same-origin or empty Origin, cookie). Body max 64 KiB.
- `q`: max 200 characters; `%`, `_` and `\` are matched literally; matching is SQLite `LIKE` (case-insensitive for ASCII).
- Do not auto-format files; touch only lines the change needs (operator rule).
- Done means `pwsh ./scripts/verify.ps1` then `pwsh ./scripts/verify-linux.ps1`, one at a time, both green.

## Review Focus

1. A session that is starred **and** archived: appears pinned in the Archived view, absent from the Active view (both lists) — T2 test.
2. Search text containing `%`, `_` or `\` (e.g. `BSI_123`, `50%`) matches literally, not as a wildcard — T2 test.
3. A newer sqlharness migrated the journal while an older dashboard runs: PATCH answers 409 and changes nothing — T3 test.
4. Clearing a title that never existed / un-starring a session without an annotation does not create an empty row — T3 test.
5. A failed save on the session page restores the previous title/star and shows the server's message instead of leaving the optimistic value — T5 test.

## File map

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/Journal/JournalSchema.cs` | `Version9` DDL, `CurrentVersion = 9` |
| `src/SqlHarness.Core/Journal/ActivityJournal.cs` | run `Version9` in `Migrate()` |
| `src/SqlHarness.Dashboard/DashboardModels.cs` | `SessionQuery`, `SessionSummary`, `SessionDetail`, `SessionAnnotation` |
| `src/SqlHarness.Dashboard/JournalReader.cs` | annotation columns, filters, note in detail |
| `src/SqlHarness.Dashboard/SessionAnnotationWriter.cs` (new) | validated, schema-checked write of one annotation |
| `src/SqlHarness.Dashboard/SessionAnnotations.cs` (new) | PATCH body parsing → writer → HTTP result |
| `src/SqlHarness.Dashboard/DashboardServer.cs` | query params, PATCH route |
| `src/SqlHarness.Dashboard/DashboardSecurity.cs` | allow the PATCH path as a guarded write |
| `ui/src/api/{types,client,queries}.ts` | types, `patchJson`, filters, mutation |
| `ui/src/lib/sessionName.ts` (new) | display-name rule |
| `ui/src/components/SessionTable.tsx` | star/archive buttons, title, note preview, pinned divider |
| `ui/src/pages/SessionsPage.tsx` | search, Active/Archived, pinned + regular lists |
| `ui/src/components/SessionAnnotationHeader.tsx`, `ui/src/components/SessionNoteCard.tsx` (new) | session page title/star/archive and note |
| `ui/src/pages/SessionPage.tsx` | uses the two components |
| `README.md`, `plans/README.md` | docs |

(`ui/` = `src/SqlHarness.Dashboard/ui/`.)

---

### Task 1: Journal schema v9 and retention

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs`, `src/SqlHarness.Core/Journal/ActivityJournal.cs` (`Migrate()`)
- Test: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, `tests/SqlHarness.Tests/Journal/JournalRetentionTests.cs`

**Interfaces:**
- Produces: table `session_annotations(session_id INTEGER PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE, title TEXT, note TEXT, starred INTEGER NOT NULL DEFAULT 0, archived_at TEXT, updated_at TEXT NOT NULL)`; `JournalSchema.CurrentVersion == 9`.

- [ ] **Step 1: Write the failing migration test** (append to `ActivityJournalTests`, next to the v6→v7 test; build the previous schema from all existing `VersionN` constants up to the current one — at `380307c` that is 1..7, after 062 include 062's constant too):

```csharp
[Fact]
public void Previous_journal_migrates_to_v9_with_an_empty_annotation_table()
{
    using var temp = new JournalTempDirectory();
    Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
    using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = JournalSchema.Version1 + JournalSchema.Version2 + JournalSchema.Version3
            + JournalSchema.Version4 + JournalSchema.Version5 + JournalSchema.Version6 + JournalSchema.Version7 + """
            INSERT INTO sessions (session_key, agent_kind, transport, source, host_pid, first_seen, last_seen)
            VALUES ('cli:v8', 'agent', 'cli', 'process-tree', 1, 't', 't');
            PRAGMA user_version = 8;
            """;
        command.ExecuteNonQuery();
    }

    Open(temp, TextWriter.Null);

    Assert.Equal(9L, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
    Assert.Equal("cli:v8", JournalDb.Rows(temp.DatabasePath, "SELECT session_key FROM sessions").Single()["session_key"]);
    Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT * FROM session_annotations"));
}
```

(If 062 took version 8 with its own constant, add it to the concatenation and keep `user_version = 8`; if the numbers moved, shift both.)

- [ ] **Step 2: Write the failing retention test** (append to `JournalRetentionTests`):

```csharp
[Fact]
public void Retention_deletes_the_annotation_with_its_session()
{
    using var temp = new JournalTempDirectory();
    Seed(temp, Now.AddDays(-40), key: "cli:old");
    Seed(temp, Now.AddDays(-1), key: "cli:new");
    using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_annotations (session_id, title, starred, updated_at)
            SELECT id, 'kept or not', 1, 't' FROM sessions;
            """;
        command.ExecuteNonQuery();
    }

    var result = JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now));

    Assert.Equal(1, result.DeletedSessions);
    var remaining = JournalDb.Rows(temp.DatabasePath,
        "SELECT s.session_key FROM session_annotations a JOIN sessions s ON s.id = a.session_id").Single();
    Assert.Equal("cli:new", remaining["session_key"]);
}
```

- [ ] **Step 3: Run and confirm RED**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Previous_journal_migrates_to_v9|FullyQualifiedName~Retention_deletes_the_annotation"`
Expected: FAIL (`no such table: session_annotations`, version 7/8 ≠ 9).

- [ ] **Step 4: Implement the schema.** In `JournalSchema.cs` set `internal const int CurrentVersion = 9;` and add after the last version constant:

```csharp
    internal const string Version9 = """
        CREATE TABLE session_annotations (
            session_id INTEGER PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
            title TEXT,
            note TEXT,
            starred INTEGER NOT NULL DEFAULT 0,
            archived_at TEXT,
            updated_at TEXT NOT NULL
        );
        """;
```

In `ActivityJournal.Migrate()` after the last `if (locked < N)` line add:

```csharp
            if (locked < 9)
                Execute(connection, JournalSchema.Version9);
```

Retention needs no code change: `JournalRetention.Connect` sets `PRAGMA foreign_keys = ON`, so `ON DELETE CASCADE` removes the annotation. The Step 2 test pins that.

- [ ] **Step 5: Fix existing version pins.** Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal|FullyQualifiedName~Dashboard"`. Tests that assert a literal previous version after migration (e.g. `Assert.Equal(7L, ...)` in the v5/v6 migration tests) must assert `(long)JournalSchema.CurrentVersion` instead. Do not weaken any other assertion.

- [ ] **Step 6: Run and confirm GREEN** — same filter as Step 5. Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Core/Journal/JournalSchema.cs src/SqlHarness.Core/Journal/ActivityJournal.cs tests/SqlHarness.Tests/Journal
git commit -m "feat(journal): add session_annotations table (schema v9)"
```

---

### Task 2: Reader exposes annotations and filters sessions

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardModels.cs`, `src/SqlHarness.Dashboard/JournalReader.cs`, `src/SqlHarness.Dashboard/DashboardServer.cs` (`/sessions` handler)
- Test: `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs`, `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`

**Interfaces:**
- Consumes: Task 1 table.
- Produces:
  - `SessionQuery(..., bool Archived = false, bool? Starred = null, string? Search = null)` (appended named parameters).
  - `SessionSummary(..., string? Title, bool Starred, bool Archived, string? NotePreview)` appended at the end. `NotePreview` = first line of the note, at most 120 characters, `\r` stripped; null without a note.
  - `SessionDetail(SessionSummary Session, IReadOnlyList<OperationSummary> Operations, string? Note)` (append `Note` after 062's fields if any).
  - `GET /api/sessions?archived=true|false&starred=true|false&q=...`; `q` longer than 200 → 400 `"q must be at most 200 characters."`.
  - `JournalReader.MaximumSearchLength = 200`.

- [ ] **Step 1: Add a test seed helper** in `tests/SqlHarness.Tests/Dashboard/JournalSeed.cs` (static, writes raw rows; tests own the database):

```csharp
    public static void Annotate(string databasePath, string sessionKey, string? title = null, string? note = null,
        bool starred = false, bool archived = false)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_annotations (session_id, title, note, starred, archived_at, updated_at)
            SELECT id, $title, $note, $starred, $archived, '2026-10-07T09:00:00.000Z' FROM sessions WHERE session_key = $key;
            """;
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$starred", starred ? 1 : 0);
        command.Parameters.AddWithValue("$archived", archived ? "2026-10-07T09:00:00.000Z" : DBNull.Value);
        command.Parameters.AddWithValue("$key", sessionKey);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
```

- [ ] **Step 2: Write failing reader tests** (append to `JournalReaderTests`; follow the file's existing pattern for creating `TempHome`, `JournalSeed` and `JournalReader` — reuse its local helper if it has one):

```csharp
[Fact]
public void Sessions_carry_annotations_and_the_detail_carries_the_note()
{
    using var home = new TempHome();
    var seed = new JournalSeed(home.DatabasePath);
    seed.Operation(JournalSeed.Session("cli:a"));
    JournalSeed.Annotate(home.DatabasePath, "cli:a", title: "BSI-1 orders", note: "first line\r\nsecond", starred: true);
    var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

    var summary = reader.Sessions(new SessionQuery(Starred: true)).Items.Single();
    Assert.Equal(("BSI-1 orders", true, false, "first line"), (summary.Title, summary.Starred, summary.Archived, summary.NotePreview));
    Assert.Equal("first line\r\nsecond", reader.Session(summary.Id)!.Note);
}

[Fact]
public void Session_lists_split_by_star_and_archive()
{
    using var home = new TempHome();
    var seed = new JournalSeed(home.DatabasePath);
    foreach (var key in new[] { "cli:plain", "cli:star", "cli:arch", "cli:star-arch" })
        seed.Operation(JournalSeed.Session(key));
    JournalSeed.Annotate(home.DatabasePath, "cli:star", starred: true);
    JournalSeed.Annotate(home.DatabasePath, "cli:arch", archived: true);
    JournalSeed.Annotate(home.DatabasePath, "cli:star-arch", starred: true, archived: true);
    var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

    string[] Keys(SessionQuery query) => reader.Sessions(query).Items.Select(s => s.SessionKey).ToArray();
    Assert.Equal(["cli:plain"], Keys(new SessionQuery(Starred: false)));
    Assert.Equal(["cli:star"], Keys(new SessionQuery(Starred: true)));
    Assert.Equal(["cli:arch"], Keys(new SessionQuery(Archived: true, Starred: false)));
    Assert.Equal(["cli:star-arch"], Keys(new SessionQuery(Archived: true, Starred: true)));
    Assert.Equal(["cli:star", "cli:plain"], Keys(new SessionQuery()));
}

[Fact]
public void Search_matches_title_note_cwd_key_and_client_title_literally()
{
    using var home = new TempHome();
    var seed = new JournalSeed(home.DatabasePath);
    seed.Operation(JournalSeed.Session("cli:title"));
    seed.Operation(JournalSeed.Session("cli:note"));
    seed.Operation(JournalSeed.Session("cli:wild"));
    JournalSeed.Annotate(home.DatabasePath, "cli:title", title: "BSI_123 orders");
    JournalSeed.Annotate(home.DatabasePath, "cli:note", note: "reads down 50% on Orders");
    JournalSeed.Annotate(home.DatabasePath, "cli:wild", title: "BSIX123");
    var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

    string[] Keys(string q) => reader.Sessions(new SessionQuery(Search: q)).Items.Select(s => s.SessionKey).Order().ToArray();
    Assert.Equal(["cli:title"], Keys("bsi_123"));
    Assert.Equal(["cli:note"], Keys("50%"));
    Assert.Equal(["cli:note", "cli:title", "cli:wild"], Keys("cli:"));
    Assert.Equal(["cli:note", "cli:title", "cli:wild"], Keys("/work"));
}

[Fact]
public void Starred_list_pages_with_the_id_cursor_without_gaps()
{
    using var home = new TempHome();
    var seed = new JournalSeed(home.DatabasePath);
    for (var i = 0; i < 120; i++)
    {
        seed.Operation(JournalSeed.Session($"cli:{i:D3}"));
        if (i % 2 == 0)
            JournalSeed.Annotate(home.DatabasePath, $"cli:{i:D3}", starred: true);
    }
    var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

    var seen = new List<string>();
    long? cursor = null;
    do
    {
        var page = reader.Sessions(new SessionQuery(Starred: true, Cursor: cursor, Limit: 25));
        seen.AddRange(page.Items.Select(s => s.SessionKey));
        cursor = page.NextCursor;
    } while (cursor is not null);

    Assert.Equal(60, seen.Count);
    Assert.Equal(seen.Count, seen.Distinct().Count());
}
```

If `JournalSeed.Session` ignores `cwd`, it is `/work` (see `JournalSeed.Session`); the `/work` assertion relies on that.

- [ ] **Step 3: Update the contract test** in `DashboardContractTests.Session_summary_names_match_the_ui`: append `null, false, false, null` to the constructor call and `"title", "starred", "archived", "notePreview"` to the expected names.

- [ ] **Step 4: Write the failing server test** (append to `DashboardServerTests`):

```csharp
[Fact]
public async Task Sessions_reject_an_overlong_search()
{
    using var home = new TempHome();
    await using var dashboard = await StartAuthenticated(home);
    var (_, client) = dashboard;

    var response = await client.GetAsync("/api/sessions?q=" + new string('a', 201));

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
}
```

- [ ] **Step 5: Run and confirm RED**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalReaderTests|FullyQualifiedName~DashboardContractTests|FullyQualifiedName~Sessions_reject_an_overlong_search"`
Expected: compile errors on the new members, then failures.

- [ ] **Step 6: Implement models** in `DashboardModels.cs`:

```csharp
public sealed record SessionQuery(
    string? Agent = null, string? Transport = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    long? Cursor = null, int Limit = JournalReader.DefaultLimit,
    bool Archived = false, bool? Starred = null, string? Search = null);
```

Append to `SessionSummary` after `string? ProtocolVersion` (and after any 062 additions): `, string? Title, bool Starred, bool Archived, string? NotePreview`. Append `, string? Note` to `SessionDetail`.

- [ ] **Step 7: Implement the reader.** In `JournalReader.cs`:

Add `internal const int MaximumSearchLength = 200;` near `DefaultLimit`.

Extend `SessionColumns` (append after `s.protocol_version`; columns become ordinals 17–20 at `380307c` — if 062 appended columns, use the next ordinals and adjust `ReadSessionRow`):

```sql
        , (SELECT a.title FROM session_annotations a WHERE a.session_id = s.id) AS title,
        COALESCE((SELECT a.starred FROM session_annotations a WHERE a.session_id = s.id), 0) AS starred,
        EXISTS (SELECT 1 FROM session_annotations a WHERE a.session_id = s.id AND a.archived_at IS NOT NULL) AS archived,
        (SELECT substr(rtrim(CASE WHEN instr(a.note, char(10)) > 0 THEN substr(a.note, 1, instr(a.note, char(10)) - 1) ELSE a.note END, char(13)), 1, 120)
         FROM session_annotations a WHERE a.session_id = s.id) AS note_preview
```

`ReadSessionRow` appends `NullableString(r, 17), r.GetInt64(18) != 0, r.GetInt64(19) != 0, NullableString(r, 20)`.

In `Sessions(SessionQuery query)` add parameters and predicates:

```csharp
            ("$archived", query.Archived ? 1 : 0),
            ("$starred", query.Starred is null ? null : query.Starred.Value ? 1 : 0),
            ("$q", query.Search is null ? null : "%" + EscapeLike(query.Search) + "%"),
```

```sql
              AND EXISTS (SELECT 1 FROM session_annotations a WHERE a.session_id = s.id AND a.archived_at IS NOT NULL) = $archived
              AND ($starred IS NULL OR COALESCE((SELECT a.starred FROM session_annotations a WHERE a.session_id = s.id), 0) = $starred)
              AND ($q IS NULL
                   OR s.session_key LIKE $q ESCAPE '\' OR s.cwd LIKE $q ESCAPE '\' OR s.client_title LIKE $q ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM session_annotations a WHERE a.session_id = s.id
                              AND (a.title LIKE $q ESCAPE '\' OR a.note LIKE $q ESCAPE '\')))
```

Change `ORDER BY s.id DESC` in `Sessions` to `ORDER BY starred DESC, s.id DESC` **only when `query.Starred is null`**; with a `Starred` filter keep `s.id DESC` (the cursor is the id). Build the ORDER BY string in C# (`var order = query.Starred is null ? "starred DESC, s.id DESC" : "s.id DESC";`). With `Starred == null` the id cursor is not exact across the star boundary; the UI always sends `starred`, and the unfiltered form exists for API callers that read the first page only — document that in an XML comment on `SessionQuery`.

```csharp
    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
```

In `Session(long id)` read the note and pass it:

```csharp
        var note = Query(connection, "SELECT note FROM session_annotations WHERE session_id = $id;", [("$id", id)],
            r => NullableString(r, 0)).FirstOrDefault();
        return new SessionDetail(WithRunning(connection, [session])[0], operations, note);
```

- [ ] **Step 8: Wire query parameters** in `DashboardServer.cs` `/sessions`:

```csharp
        MapRead(api, "/sessions", (string? agent, string? transport, string? from, string? to, long? cursor, int? limit,
            bool? archived, bool? starred, string? q) =>
            q is { Length: > JournalReader.MaximumSearchLength }
                ? BadRequest("q must be at most 200 characters.")
                : TryWindow(from, to, out var window)
                    ? Results.Json(reader.Sessions(new SessionQuery(agent, transport, window.From, window.To, cursor,
                        limit ?? JournalReader.DefaultLimit, archived ?? false, starred, string.IsNullOrWhiteSpace(q) ? null : q.Trim())), Json)
                    : BadRequest("from/to must be ISO-8601 timestamps."));
```

- [ ] **Step 9: Run and confirm GREEN** — Step 5 filter plus `FullyQualifiedName~LiveFeedTests` (live feed uses `SessionColumns`). Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard
git commit -m "feat(dashboard): expose session annotations, star/archive filters and search"
```

---

### Task 3: Guarded PATCH writes an annotation

**Files:**
- Create: `src/SqlHarness.Dashboard/SessionAnnotationWriter.cs`, `src/SqlHarness.Dashboard/SessionAnnotations.cs`
- Modify: `src/SqlHarness.Dashboard/DashboardModels.cs`, `src/SqlHarness.Dashboard/DashboardServer.cs`, `src/SqlHarness.Dashboard/DashboardSecurity.cs`
- Test: create `tests/SqlHarness.Tests/Dashboard/SessionAnnotationsTests.cs`; modify `DashboardServerTests.Only_get_endpoints_and_the_two_guarded_writes_exist`

**Interfaces:**
- Consumes: Task 1 table, Task 2 models.
- Produces:
  - `public sealed record SessionAnnotation(string? Title, string? Note, bool Starred, bool Archived);` (DashboardModels).
  - `internal sealed record AnnotationPatch(bool SetTitle, string? Title, bool SetNote, string? Note, bool? Starred, bool? Archived);`
  - `internal enum AnnotationWriteStatus { Updated, NotFound, SchemaMismatch, Busy }`
  - `internal sealed class SessionAnnotationWriter(string databasePath, TimeProvider time)` with `(AnnotationWriteStatus Status, SessionAnnotation? Annotation) Apply(long sessionId, AnnotationPatch patch)`.
  - `internal static class SessionAnnotations` with `Task<IResult> PatchAsync(HttpContext context, long id, SessionAnnotationWriter writer, JsonSerializerOptions json)` and `internal static (AnnotationPatch? Patch, string? Error) Parse(byte[] body)`.
  - `PATCH /api/sessions/{id}/annotation` → 200 `SessionAnnotation`; 400 `{ "error": "..." }`; 404; 409 `{ "error": "..." }`; 503 `{ "error": "The journal is busy; try again." }`.
  - `DashboardServerOptions.Time` (`TimeProvider`, default `TimeProvider.System`).

- [ ] **Step 1: Write failing tests** in `SessionAnnotationsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class SessionAnnotationsTests
{
    private static HttpRequestMessage Patch(long id, string json, bool guarded = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/sessions/{id}/annotation")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (guarded)
            request.Headers.Add(DashboardSecurity.WriteHeader, "1");
        return request;
    }

    private static long SeedSession(TempHome home, string key = "cli:a")
    {
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session(key));
        return (long)Journal.JournalDb.Rows(home.DatabasePath, $"SELECT id FROM sessions WHERE session_key = '{key}'").Single()["id"]!;
    }

    private static List<Dictionary<string, object?>> Annotations(TempHome home) =>
        Journal.JournalDb.Rows(home.DatabasePath, "SELECT * FROM session_annotations");

    [Fact]
    public async Task Patch_sets_fields_independently_and_returns_the_annotation()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var first = await client.SendAsync(Patch(id, """{"title":"  BSI-1 orders  ","starred":true}"""));
        var second = await client.SendAsync(Patch(id, """{"note":"line 1\nline 2"}"""));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var annotation = await second.Content.ReadFromJsonAsync<SessionAnnotation>();
        Assert.Equal(new SessionAnnotation("BSI-1 orders", "line 1\nline 2", true, false), annotation);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    }

    [Fact]
    public async Task Archiving_twice_keeps_the_first_time_and_unarchive_clears_it()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        var time = new Journal.FixedTimeProvider(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero));
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home, o => o with { Time = time });
        var (_, client) = dashboard;

        await client.SendAsync(Patch(id, """{"archived":true,"note":"keep"}"""));
        time.Now = time.Now.AddHours(1);
        await client.SendAsync(Patch(id, """{"archived":true}"""));
        Assert.Equal("2026-10-14T10:00:00.000Z", Annotations(home).Single()["archived_at"]);

        await client.SendAsync(Patch(id, """{"archived":false}"""));
        Assert.Null(Annotations(home).Single()["archived_at"]);
    }

    [Fact]
    public async Task Clearing_everything_leaves_no_empty_row_and_noop_unstar_creates_none()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Patch(id, """{"starred":false,"title":"   "}"""))).StatusCode);
        Assert.Empty(Annotations(home));

        await client.SendAsync(Patch(id, """{"title":"x"}"""));
        await client.SendAsync(Patch(id, """{"title":null}"""));
        Assert.Empty(Annotations(home));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"color":"red"}""")]
    [InlineData("""{"starred":"yes"}""")]
    [InlineData("""{"title":42}""")]
    [InlineData("""{"title":"a\nb"}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public async Task Invalid_bodies_are_rejected_without_writing(string body)
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Patch(id, body))).StatusCode);
        Assert.Empty(Annotations(home));
    }

    [Fact]
    public async Task Length_limits_are_enforced()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Patch(id, $$"""{"title":"{{new string('t', 200)}}","note":"{{new string('n', 10_000)}}"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Patch(id, $$"""{"title":"{{new string('t', 201)}}"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Patch(id, $$"""{"note":"{{new string('n', 10_001)}}"}"""))).StatusCode);
    }

    [Fact]
    public async Task Unknown_session_is_404()
    {
        using var home = new TempHome();
        SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Patch(999, """{"starred":true}"""))).StatusCode);
    }

    [Fact]
    public async Task A_journal_migrated_by_a_newer_sqlharness_is_refused_with_409()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;
        using (var connection = new SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var bump = connection.CreateCommand();
            bump.CommandText = $"PRAGMA user_version = {JournalSchema.CurrentVersion + 1};";
            bump.ExecuteNonQuery();
        }

        var response = await client.SendAsync(Patch(id, """{"starred":true}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("schema", await response.Content.ReadAsStringAsync());
        Assert.Empty(Annotations(home));
    }

    [Fact]
    public async Task Patch_requires_the_write_protections()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Patch(id, """{"starred":true}""", guarded: false))).StatusCode);
        var foreign = Patch(id, """{"starred":true}""");
        foreign.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Patch(id, """{"starred":true}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            (await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/sessions/{id}") { Content = JsonContent.Create(new { }) })).StatusCode);
        Assert.Empty(Annotations(home));
    }
}
```

Check `FixedTimeProvider` lives in `SqlHarness.Tests.Journal` and has a settable `Now` (it does in `JournalSeed`); adjust the namespace qualifier if it differs. `JournalDb` is in `SqlHarness.Tests.Journal`.

In `DashboardServerTests.Only_get_endpoints_and_the_two_guarded_writes_exist` rename to `Only_get_endpoints_and_the_three_guarded_writes_exist`, expect `3` writes and add:

```csharp
        var annotationPatch = Assert.Single(writes, endpoint => endpoint.Route == "/api/sessions/{id:long}/annotation");
        Assert.Equal(["PATCH"], annotationPatch.Methods);
```

(Match the route string to what `server.Endpoints` reports; read the existing helper if unsure.)

- [ ] **Step 2: Run and confirm RED**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SessionAnnotationsTests|FullyQualifiedName~guarded_writes_exist"`
Expected: compile errors, then 405/404 failures.

- [ ] **Step 3: Add `SessionAnnotation`** to `DashboardModels.cs` and `Time` to `DashboardServerOptions`:

```csharp
public sealed record SessionAnnotation(string? Title, string? Note, bool Starred, bool Archived);
```

```csharp
    /// <summary>Clock for operator writes (annotation timestamps).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
```

- [ ] **Step 4: Implement the writer** `SessionAnnotationWriter.cs`:

```csharp
using System.Globalization;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

internal sealed record AnnotationPatch(bool SetTitle, string? Title, bool SetNote, string? Note, bool? Starred, bool? Archived);

internal enum AnnotationWriteStatus { Updated, NotFound, SchemaMismatch, Busy }

/// <summary>
/// The dashboard's only journal write: one operator annotation per session, in one short
/// IMMEDIATE transaction. Refuses when the journal schema is not the one this binary knows;
/// the dashboard never migrates. An annotation with nothing left in it is deleted.
/// </summary>
internal sealed class SessionAnnotationWriter(string databasePath, TimeProvider time)
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    internal (AnnotationWriteStatus Status, SessionAnnotation? Annotation) Apply(long sessionId, AnnotationPatch patch)
    {
        if (!File.Exists(databasePath))
            return (AnnotationWriteStatus.NotFound, null);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON;");
            Execute(connection, "BEGIN IMMEDIATE;");
            try
            {
                var result = ApplyLocked(connection, sessionId, patch);
                Execute(connection, result.Status == AnnotationWriteStatus.Updated ? "COMMIT;" : "ROLLBACK;");
                return result;
            }
            catch
            {
                Execute(connection, "ROLLBACK;");
                throw;
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            return (AnnotationWriteStatus.Busy, null);
        }
    }

    private (AnnotationWriteStatus, SessionAnnotation?) ApplyLocked(SqliteConnection connection, long sessionId, AnnotationPatch patch)
    {
        if ((long)Scalar(connection, "PRAGMA user_version;")! != JournalSchema.CurrentVersion)
            return (AnnotationWriteStatus.SchemaMismatch, null);
        if (Scalar(connection, "SELECT 1 FROM sessions WHERE id = $id;", ("$id", sessionId)) is null)
            return (AnnotationWriteStatus.NotFound, null);

        var now = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        Execute(connection, """
            INSERT INTO session_annotations (session_id, updated_at) VALUES ($id, $now) ON CONFLICT(session_id) DO NOTHING;
            UPDATE session_annotations SET
                title = CASE WHEN $setTitle THEN $title ELSE title END,
                note = CASE WHEN $setNote THEN $note ELSE note END,
                starred = COALESCE($starred, starred),
                archived_at = CASE WHEN $archived IS NULL THEN archived_at WHEN $archived = 1 THEN COALESCE(archived_at, $now) ELSE NULL END,
                updated_at = $now
            WHERE session_id = $id;
            DELETE FROM session_annotations
            WHERE session_id = $id AND title IS NULL AND note IS NULL AND starred = 0 AND archived_at IS NULL;
            """,
            ("$id", sessionId), ("$now", now),
            ("$setTitle", patch.SetTitle ? 1 : 0), ("$title", patch.Title),
            ("$setNote", patch.SetNote ? 1 : 0), ("$note", patch.Note),
            ("$starred", patch.Starred is null ? null : patch.Starred.Value ? 1 : 0),
            ("$archived", patch.Archived is null ? null : patch.Archived.Value ? 1 : 0));

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT title, note, starred, archived_at IS NOT NULL FROM session_annotations WHERE session_id = $id;";
        read.Parameters.AddWithValue("$id", sessionId);
        using var reader = read.ExecuteReader();
        var annotation = reader.Read()
            ? new SessionAnnotation(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2) != 0, reader.GetInt64(3) != 0)
            : new SessionAnnotation(null, null, false, false);
        return (AnnotationWriteStatus.Updated, annotation);
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
```

- [ ] **Step 5: Implement parsing and the endpoint** `SessionAnnotations.cs`:

```csharp
using System.Text.Json;

using Microsoft.AspNetCore.Http;

namespace SqlHarness.Dashboard;

/// <summary>PATCH /api/sessions/{id}/annotation: a partial, strictly validated operator annotation.</summary>
internal static class SessionAnnotations
{
    internal const int MaximumTitleLength = 200;
    internal const int MaximumNoteLength = 10_000;
    internal const int MaximumBodyBytes = 64 * 1024;

    internal static async Task<IResult> PatchAsync(HttpContext context, long id, SessionAnnotationWriter writer, JsonSerializerOptions json)
    {
        var body = await ReadBodyAsync(context.Request, context.RequestAborted);
        if (body is null)
            return Error("The annotation body is larger than 64 KiB.", StatusCodes.Status400BadRequest, json);
        var (patch, error) = Parse(body);
        if (patch is null)
            return Error(error!, StatusCodes.Status400BadRequest, json);

        var (status, annotation) = writer.Apply(id, patch);
        return status switch
        {
            AnnotationWriteStatus.Updated => Results.Json(annotation, json),
            AnnotationWriteStatus.NotFound => Results.NotFound(),
            AnnotationWriteStatus.SchemaMismatch => Error(
                "The activity journal schema does not match this dashboard; restart the dashboard with the current sqlharness.",
                StatusCodes.Status409Conflict, json),
            _ => Error("The journal is busy; try again.", StatusCodes.Status503ServiceUnavailable, json),
        };
    }

    internal static (AnnotationPatch? Patch, string? Error) Parse(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (null, "The body must be a JSON object.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "The body must be a JSON object.");
            bool setTitle = false, setNote = false;
            string? title = null, note = null;
            bool? starred = null, archived = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "title":
                        if (!TryText(property.Value, out title))
                            return (null, "title must be a string or null.");
                        title = title?.Trim();
                        if (string.IsNullOrEmpty(title))
                            title = null;
                        else if (title.Length > MaximumTitleLength)
                            return (null, "title must be at most 200 characters.");
                        else if (title.Contains('\n') || title.Contains('\r'))
                            return (null, "title must be one line.");
                        setTitle = true;
                        break;
                    case "note":
                        if (!TryText(property.Value, out note))
                            return (null, "note must be a string or null.");
                        if (string.IsNullOrWhiteSpace(note))
                            note = null;
                        else if (note.Length > MaximumNoteLength)
                            return (null, "note must be at most 10000 characters.");
                        setNote = true;
                        break;
                    case "starred":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return (null, "starred must be a boolean.");
                        starred = property.Value.GetBoolean();
                        break;
                    case "archived":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return (null, "archived must be a boolean.");
                        archived = property.Value.GetBoolean();
                        break;
                    default:
                        return (null, $"Unknown field: {property.Name}.");
                }
            }

            if (!setTitle && !setNote && starred is null && archived is null)
                return (null, "Nothing to change.");
            return (new AnnotationPatch(setTitle, title, setNote, note, starred, archived), null);
        }
    }

    private static bool TryText(JsonElement value, out string? text)
    {
        text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Null;
    }

    private static IResult Error(string message, int status, JsonSerializerOptions json) =>
        Results.Json(new { error = message }, json, statusCode: status);

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
```

- [ ] **Step 6: Register the route** in `DashboardServer.cs` next to the settings `PUT` (before the `/{**rest}` catch-all):

```csharp
        var annotationWriter = new SessionAnnotationWriter(options.DatabasePath, options.Time);
        api.MapMethods("/sessions/{id:long}/annotation", [HttpMethods.Patch], (Delegate)((HttpContext context, long id) =>
            SessionAnnotations.PatchAsync(context, id, annotationWriter, Json)));
```

- [ ] **Step 7: Allow the guarded write** in `DashboardSecurity.cs`. Add next to the other path constants and update the class summary ("three guarded writes … PATCH /api/sessions/{id}/annotation"):

```csharp
    private static readonly System.Text.RegularExpressions.Regex AnnotationPath =
        new(@"^/api/sessions/[0-9]{1,19}/annotation$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
```

In `GuardAsync`:

```csharp
        var isAnnotationWrite = HttpMethods.IsPatch(context.Request.Method)
            && AnnotationPath.IsMatch(context.Request.Path.Value ?? string.Empty);
        var isWrite = isSettingsWrite || isPricesRefresh || isAnnotationWrite;
```

- [ ] **Step 8: Run and confirm GREEN** — Step 2 filter, then the whole `FullyQualifiedName~Dashboard` filter. Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard
git commit -m "feat(dashboard): guarded PATCH for session annotations"
```

---

### Task 4: Sessions list — search, Active/Archived, pinned, star and archive

**Files:**
- Modify: `ui/src/api/types.ts`, `ui/src/api/client.ts`, `ui/src/api/queries.ts`, `ui/src/components/SessionTable.tsx`, `ui/src/pages/SessionsPage.tsx`, `ui/src/test/fixtures.ts`
- Create: `ui/src/lib/sessionName.ts`, `ui/src/lib/sessionName.test.ts`
- Test: `ui/src/pages/SessionsPage.test.tsx`

**Interfaces:**
- Consumes: Task 2 query parameters and fields; Task 3 PATCH.
- Produces (TS):
  - `SessionSummary` gains `title: string | null; starred: boolean; archived: boolean; notePreview: string | null`; `SessionDetail` gains `note: string | null`.
  - `export type SessionAnnotation = { title: string | null; note: string | null; starred: boolean; archived: boolean }`
  - `export type AnnotationPatch = Partial<{ title: string | null; note: string | null; starred: boolean; archived: boolean }>`
  - `export class RequestError extends Error` (message = server `error` text) in `client.ts`; `patchJson<T>(path, body): Promise<T>`.
  - `SessionFilters = { agent?: string; transport?: string; archived?: boolean; starred?: boolean; q?: string }`.
  - `useUpdateSessionAnnotation(id: number)` mutation: `mutate(patch: AnnotationPatch)`; optimistic update of `queryKeys.session(id)`, rollback on error, invalidates `["sessions"]` and `queryKeys.session(id)` on settle.
  - `sessionDisplayName(s: Pick<SessionSummary, "title" | "sessionKey">): string` — `s.title ?? s.sessionKey`.

- [ ] **Step 1: Fixture defaults.** In `ui/src/test/fixtures.ts` `session()` add `title: null, starred: false, archived: false, notePreview: null` to the defaults.

- [ ] **Step 2: Write failing tests.** `ui/src/lib/sessionName.test.ts`:

```ts
import { expect, test } from "vitest"
import { sessionDisplayName } from "./sessionName"

test("title wins over the session key", () => {
  expect(sessionDisplayName({ title: "BSI-1 orders", sessionKey: "cli:a" })).toBe("BSI-1 orders")
  expect(sessionDisplayName({ title: null, sessionKey: "cli:a" })).toBe("cli:a")
})
```

Replace the URL keys in the existing `SessionsPage.test.tsx` tests: the page now always requests both lists, e.g. `"/api/sessions?archived=false&starred=true&limit=50"` and `"/api/sessions?archived=false&starred=false&limit=50"` (parameter order follows the object order passed to `withQuery`; keep `cursor` before `limit` as today). Then append:

```tsx
test("starred sessions are pinned above the others and show titles", async () => {
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [session({ id: 5, sessionKey: "cli:pin", title: "BSI-1 orders", starred: true })], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 6, sessionKey: "cli:plain" })], nextCursor: null },
  })
  renderApp("/sessions")
  const pinned = await screen.findByText("BSI-1 orders")
  const plain = await screen.findByText("cli:plain")
  expect(pinned.compareDocumentPosition(plain) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(screen.getByText("cli:pin")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Unstar session cli:pin" })).toBeInTheDocument()
})

test("archived view and search change the query", async () => {
  const calls = stubFetch({
    "/api/agents": [],
    "/api/sessions": { items: [], nextCursor: null },
  })
  renderApp("/sessions")
  await userEvent.click(await screen.findByRole("tab", { name: "Archived" }))
  expect(calls).toContain("/api/sessions?archived=true&starred=false&limit=50")
  await userEvent.type(screen.getByRole("searchbox", { name: "Search sessions" }), "BSI_1")
  await waitFor(() => expect(calls).toContain("/api/sessions?archived=true&starred=false&q=BSI_1&limit=50"))
})

test("star and archive buttons patch the annotation and refetch", async () => {
  const patches: { url: string; body: unknown }[] = []
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 7, sessionKey: "cli:x" })], nextCursor: null },
    "/api/sessions/7/annotation": (url: URL, init?: RequestInit) => {
      patches.push({ url: url.pathname, body: JSON.parse(String(init?.body)) })
      expect(init?.method).toBe("PATCH")
      expect((init?.headers as Record<string, string>)["X-SqlHarness-Dashboard"]).toBe("1")
      return new Response(JSON.stringify({ title: null, note: null, starred: true, archived: false }), { status: 200 })
    },
  })
  renderApp("/sessions")
  await userEvent.click(await screen.findByRole("button", { name: "Star session cli:x" }))
  // One mutation per row: its buttons are disabled while a save is pending.
  await waitFor(() => expect(patches).toHaveLength(1))
  await waitFor(() => expect(screen.getByRole("button", { name: "Archive session cli:x" })).toBeEnabled())
  await userEvent.click(screen.getByRole("button", { name: "Archive session cli:x" }))
  await waitFor(() => expect(patches.map(p => p.body)).toEqual([{ starred: true }, { archived: true }]))
})

test("a note shows an icon with its first line", async () => {
  stubFetch({
    "/api/agents": [],
    "/api/sessions?archived=false&starred=true&limit=50": { items: [], nextCursor: null },
    "/api/sessions?archived=false&starred=false&limit=50": { items: [session({ id: 8, sessionKey: "cli:n", notePreview: "reads down 40%" })], nextCursor: null },
  })
  renderApp("/sessions")
  expect(await screen.findByLabelText("Note: reads down 40%")).toBeInTheDocument()
})
```

Add `waitFor` to the `@testing-library/react` import.

- [ ] **Step 3: Run and confirm RED**

Run (in `src/SqlHarness.Dashboard/ui`): `npm run test -- src/pages/SessionsPage.test.tsx src/lib/sessionName.test.ts`
Expected: FAIL.

- [ ] **Step 4: Types and client.** `types.ts`: add the fields and types from **Interfaces**. `client.ts`, after `postJson`:

```ts
/** A write whose failure carries the server's `error` text, shown to the operator as is. */
export class RequestError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "RequestError"
  }
}

export async function patchJson<T>(path: string, body: unknown): Promise<T> {
  const response = await send(path, {
    method: "PATCH",
    credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json", "X-SqlHarness-Dashboard": "1" },
    body: JSON.stringify(body),
  })
  if (response.status === 401) throw new UnauthorizedError()
  if (response.status === 404) throw new NotFoundError(path)
  if (!response.ok) {
    const error = await response.json().then((data: { error?: string }) => data.error, () => undefined)
    throw new RequestError(error ?? `Request failed with ${response.status}: ${path}`)
  }
  return (await response.json()) as T
}
```

Update the comment on `putJson` ("The dashboard's only write") to "A settings write; …".

- [ ] **Step 5: Queries.** In `queries.ts`:

```ts
export type SessionFilters = { agent?: string; transport?: string; archived?: boolean; starred?: boolean; q?: string }

const flag = (value: boolean | undefined) => (value === undefined ? undefined : String(value))

export function useSessions(filters: SessionFilters) {
  return useInfiniteQuery({
    queryKey: queryKeys.sessions(filters),
    queryFn: ({ pageParam }) => getJson<Page<SessionSummary>>("/api/sessions", {
      agent: filters.agent, transport: filters.transport,
      archived: flag(filters.archived), starred: flag(filters.starred), q: filters.q,
      cursor: pageParam, limit: 50,
    }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: last => last.nextCursor ?? undefined,
  })
}

export function useUpdateSessionAnnotation(id: number) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (patch: AnnotationPatch) => patchJson<SessionAnnotation>(`/api/sessions/${id}/annotation`, patch),
    onMutate: async patch => {
      await client.cancelQueries({ queryKey: queryKeys.session(id) })
      const previous = client.getQueryData<SessionDetail>(queryKeys.session(id))
      if (previous) {
        client.setQueryData<SessionDetail>(queryKeys.session(id), {
          ...previous,
          note: patch.note !== undefined ? patch.note : previous.note,
          session: {
            ...previous.session,
            ...(patch.title !== undefined ? { title: patch.title } : {}),
            ...(patch.starred !== undefined ? { starred: patch.starred } : {}),
            ...(patch.archived !== undefined ? { archived: patch.archived } : {}),
          },
        })
      }
      return { previous }
    },
    onError: (_error, _patch, context) => {
      if (context?.previous) client.setQueryData(queryKeys.session(id), context.previous)
    },
    onSettled: () => {
      void client.invalidateQueries({ queryKey: ["sessions"] })
      void client.invalidateQueries({ queryKey: queryKeys.session(id) })
    },
  })
}
```

Add the needed imports (`patchJson`, `AnnotationPatch`, `SessionAnnotation`). Keep `SessionsPage`'s agent `Tabs` working: the existing callers pass `{ agent }` only.

- [ ] **Step 6: Display name helper** `ui/src/lib/sessionName.ts`:

```ts
import type { SessionSummary } from "@/api/types"

/** Operator title, else the session key. Spec C inserts the agent's conversation title between the two. */
export function sessionDisplayName(session: Pick<SessionSummary, "title" | "sessionKey">): string {
  return session.title ?? session.sessionKey
}
```

- [ ] **Step 7: SessionTable.** Change the signature to `SessionTable({ pinned, sessions, now }: { pinned: SessionSummary[]; sessions: SessionSummary[]; now: number })`. Add a leading header cell `<TableHead className="w-8"><span className="sr-only">Star</span></TableHead>` and a trailing `<TableHead className="w-8"><span className="sr-only">Actions</span></TableHead>`. Render `pinned` rows, then — only when both lists are non-empty — a divider row `<TableRow aria-hidden="true"><TableCell colSpan={10} className="h-0 border-b-2 p-0" /></TableRow>`, then `sessions` rows. Extract the row to a `SessionRow` component in the same file:

```tsx
function SessionRow({ session, now }: { session: SessionSummary; now: number }) {
  const update = useUpdateSessionAnnotation(session.id)
  const name = sessionDisplayName(session)
  return (
    <TableRow>
      <TableCell>
        <Button variant="ghost" size="icon" disabled={update.isPending}
          aria-label={`${session.starred ? "Unstar" : "Star"} session ${session.sessionKey}`}
          aria-pressed={session.starred}
          onClick={() => update.mutate({ starred: !session.starred })}>
          <Star className={session.starred ? "fill-current" : undefined} />
        </Button>
      </TableCell>
      <TableCell>
        <div className="flex items-center gap-1">
          <Link to="/sessions/$id" params={{ id: String(session.id) }} className={session.title ? undefined : "font-mono"}>
            {name}
          </Link>
          {session.notePreview && (
            <Tooltip>
              <TooltipTrigger aria-label={`Note: ${session.notePreview}`}><StickyNote className="size-4 text-muted-foreground" /></TooltipTrigger>
              <TooltipContent>{session.notePreview}</TooltipContent>
            </Tooltip>
          )}
        </div>
        {session.title && <div className="font-mono text-sm text-muted-foreground">{session.sessionKey}</div>}
      </TableCell>
      {/* existing Agent … State cells unchanged */}
      <TableCell>
        <Button variant="ghost" size="icon" disabled={update.isPending}
          aria-label={`${session.archived ? "Unarchive" : "Archive"} session ${session.sessionKey}`}
          onClick={() => update.mutate({ archived: !session.archived })}>
          {session.archived ? <ArchiveRestore /> : <Archive />}
        </Button>
      </TableCell>
    </TableRow>
  )
}
```

Icons from `lucide-react` (`Star`, `StickyNote`, `Archive`, `ArchiveRestore`). Check `components/ui/tooltip.tsx` for the exported names and whether a provider is required; follow how other components use it (`grep -rn "Tooltip" ui/src/components`). Keep the existing cells' markup as is.

- [ ] **Step 8: SessionsPage.** Add state and the second query:

```tsx
  const [view, setView] = useState<"active" | "archived">("active")
  const [search, setSearch] = useState("")
  const q = useDebounced(search.trim(), 300)
  const base = { ...(selected === "all" ? {} : { agent: selected }), archived: view === "archived", q: q || undefined }
  const pinned = useSessions({ ...base, starred: true })
  const sessions = useSessions({ ...base, starred: false })
```

`useDebounced` as a small hook in the same file:

```tsx
function useDebounced<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), ms)
    return () => clearTimeout(timer)
  }, [value, ms])
  return debounced
}
```

Above the agent tabs, a row with `<Input type="search" aria-label="Search sessions" placeholder="Search title, note, directory, key" value={search} onChange={e => setSearch(e.target.value)} className="max-w-sm" />` and `<Tabs value={view} onValueChange={v => setView(v as "active" | "archived")}><TabsList><TabsTrigger value="active">Active</TabsTrigger><TabsTrigger value="archived">Archived</TabsTrigger></TabsList></Tabs>`. Card body: error if either query errored, skeleton while either is pending, otherwise `<SessionTable pinned={pinned.data.pages.flatMap(p => p.items)} sessions={sessions.data.pages.flatMap(p => p.items)} now={now} />`; a "Load more pinned" button when `pinned.hasNextPage`, the existing "Load more" for `sessions`. Card title: "Archived sessions" in the archived view. Empty state when both are empty: "No sessions" (or "No sessions match the search" with a search).

- [ ] **Step 9: Run and confirm GREEN**, then the whole UI suite: `npm run test` and `npm run typecheck`. Fix other tests that build `SessionSummary` objects by hand (the fixture defaults cover most).

- [ ] **Step 10: Commit**

```bash
git add src/SqlHarness.Dashboard/ui/src
git commit -m "feat(dashboard-ui): pinned, searchable and archivable sessions list"
```

---

### Task 5: Session page — title, star, archive and note

**Files:**
- Create: `ui/src/components/SessionAnnotationHeader.tsx`, `ui/src/components/SessionNoteCard.tsx`
- Modify: `ui/src/pages/SessionPage.tsx`
- Test: `ui/src/pages/SessionPage.test.tsx`

**Interfaces:**
- Consumes: `useUpdateSessionAnnotation`, `RequestError`, `sessionDisplayName` (Task 4), `SessionDetail.note`.
- Produces: `SessionAnnotationHeader({ session }: { session: SessionSummary })`, `SessionNoteCard({ sessionId, note }: { sessionId: number; note: string | null })`.

- [ ] **Step 1: Write failing tests** (append to `SessionPage.test.tsx`; reuse its existing detail fixture/URL for `/api/sessions/1`, adding `note: null` to any hand-written `SessionDetail`):

```tsx
test("edits the title inline: Enter saves, Escape cancels, empty clears", async () => {
  const bodies: unknown[] = []
  let title: string | null = null
  stubFetch({
    // The detail is refetched after each save, so it must reflect the saved state.
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1, sessionKey: "cli:abc", title }), operations: [], note: null }), { status: 200 }),
    "/api/sessions/1/annotation": (_url: URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as { title: string | null }
      bodies.push(body)
      title = body.title
      return new Response(JSON.stringify({ title, note: null, starred: false, archived: false }), { status: 200 })
    },
  })
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Edit title" }))
  await userEvent.type(screen.getByRole("textbox", { name: "Session title" }), "draft{Escape}")
  expect(bodies).toEqual([])
  await userEvent.click(screen.getByRole("button", { name: "Edit title" }))
  await userEvent.type(screen.getByRole("textbox", { name: "Session title" }), "BSI-1{Enter}")
  expect(await screen.findByRole("heading", { level: 1, name: "BSI-1" })).toBeInTheDocument()
  await waitFor(() => expect(bodies).toEqual([{ title: "BSI-1" }]))
})

test("a failed save rolls back and shows the server message", async () => {
  stubFetch({
    "/api/sessions/1": { session: session({ id: 1, sessionKey: "cli:abc", starred: false }), operations: [], note: null },
    "/api/sessions/1/annotation": () =>
      new Response(JSON.stringify({ error: "The journal is busy; try again." }), { status: 503 }),
  })
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Star session" }))
  expect(await screen.findByText("The journal is busy; try again.")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Star session" })).toHaveAttribute("aria-pressed", "false")
})

test("archive shows a badge and the button turns into Unarchive", async () => {
  let archived = false
  stubFetch({
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1, archived }), operations: [], note: null }), { status: 200 }),
    "/api/sessions/1/annotation": () => {
      archived = true
      return new Response(JSON.stringify({ title: null, note: null, starred: false, archived: true }), { status: 200 })
    },
  })
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Archive" }))
  expect(await screen.findByText("Archived")).toBeInTheDocument()
  expect(screen.getByRole("button", { name: "Unarchive" })).toBeInTheDocument()
})

test("adds, counts and saves a note", async () => {
  const bodies: unknown[] = []
  let note: string | null = null
  stubFetch({
    "/api/sessions/1": () => new Response(JSON.stringify({ session: session({ id: 1 }), operations: [], note }), { status: 200 }),
    "/api/sessions/1/annotation": (_url: URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as { note: string | null }
      bodies.push(body)
      note = body.note
      return new Response(JSON.stringify({ title: null, note, starred: false, archived: false }), { status: 200 })
    },
  })
  renderApp("/sessions/1")
  await userEvent.click(await screen.findByRole("button", { name: "Add note" }))
  await userEvent.type(screen.getByRole("textbox", { name: "Session note" }), "reads -40%")
  expect(screen.getByText("10 / 10000")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Save note" }))
  expect(await screen.findByText("Saved")).toBeInTheDocument()
  expect(bodies).toEqual([{ note: "reads -40%" }])
})
```

- [ ] **Step 2: Run and confirm RED** — `npm run test -- src/pages/SessionPage.test.tsx`.

- [ ] **Step 3: `SessionAnnotationHeader.tsx`:**

```tsx
import { useState } from "react"
import { Archive, ArchiveRestore, Pencil, Star } from "lucide-react"
import type { SessionSummary } from "@/api/types"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"

export function SessionAnnotationHeader({ session }: { session: SessionSummary }) {
  const update = useUpdateSessionAnnotation(session.id)
  const [draft, setDraft] = useState<string | null>(null)

  const save = () => {
    const title = draft?.trim() ?? ""
    setDraft(null)
    if ((title || null) !== session.title) update.mutate({ title: title || null })
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        {draft === null ? (
          <>
            <h1>{session.title ?? `Session ${session.id}`}</h1>
            <Button variant="ghost" size="icon" aria-label="Edit title" onClick={() => setDraft(session.title ?? "")}>
              <Pencil />
            </Button>
          </>
        ) : (
          <Input
            autoFocus aria-label="Session title" maxLength={200} value={draft} placeholder={session.sessionKey}
            onChange={event => setDraft(event.target.value)}
            onKeyDown={event => {
              if (event.key === "Enter") save()
              if (event.key === "Escape") setDraft(null)
            }}
            onBlur={() => setDraft(null)}
            className="max-w-xl"
          />
        )}
        <Button variant="ghost" size="icon" aria-label={session.starred ? "Unstar session" : "Star session"}
          aria-pressed={session.starred} disabled={update.isPending}
          onClick={() => update.mutate({ starred: !session.starred })}>
          <Star className={session.starred ? "fill-current" : undefined} />
        </Button>
        <Button variant="outline" size="sm" disabled={update.isPending}
          onClick={() => update.mutate({ archived: !session.archived })}>
          {session.archived ? <ArchiveRestore /> : <Archive />}
          {session.archived ? "Unarchive" : "Archive"}
        </Button>
        {session.archived && <Badge variant="outline">Archived</Badge>}
      </div>
      {update.error && (
        <Alert variant="destructive">
          <AlertDescription>{update.error.message}</AlertDescription>
        </Alert>
      )}
    </div>
  )
}
```

Check `ui/alert.tsx` exports (`Alert`, `AlertDescription`) and `button.tsx` sizes (`icon`, `sm`); adapt names if they differ. `onBlur` cancels: the Enter handler runs first, so a save is not lost.

- [ ] **Step 4: `SessionNoteCard.tsx`:**

```tsx
import { useState } from "react"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"

const maximumNoteLength = 10_000

export function SessionNoteCard({ sessionId, note }: { sessionId: number; note: string | null }) {
  const update = useUpdateSessionAnnotation(sessionId)
  const [draft, setDraft] = useState<string | null>(null)

  if (note === null && draft === null)
    return <Button variant="outline" onClick={() => { update.reset(); setDraft("") }}>Add note</Button>

  return (
    <Card>
      <CardHeader><CardTitle><h2>Note</h2></CardTitle></CardHeader>
      <CardContent className="space-y-2">
        {draft === null ? (
          <>
            <p className="whitespace-pre-wrap break-words">{note}</p>
            <div className="flex items-center gap-2">
              <Button variant="outline" size="sm" onClick={() => { update.reset(); setDraft(note ?? "") }}>Edit note</Button>
              {update.isSuccess && <span className="text-sm text-muted-foreground">Saved</span>}
            </div>
          </>
        ) : (
          <>
            <textarea
              aria-label="Session note" maxLength={maximumNoteLength} value={draft} rows={6}
              onChange={event => setDraft(event.target.value)}
              className="w-full rounded-md border bg-transparent p-2 text-sm"
            />
            <div className="flex items-center gap-2">
              <Button size="sm" aria-label="Save note" disabled={update.isPending}
                onClick={() => update.mutate({ note: draft.trim() ? draft : null }, { onSuccess: () => setDraft(null) })}>
                Save
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setDraft(null)}>Cancel</Button>
              <span className="ml-auto text-sm text-muted-foreground">{draft.length} / {maximumNoteLength}</span>
            </div>
          </>
        )}
        {update.error && (
          <Alert variant="destructive"><AlertDescription>{update.error.message}</AlertDescription></Alert>
        )}
      </CardContent>
    </Card>
  )
}
```

Match the `textarea` classes to `components/ui/input.tsx` styling (copy its border/focus classes) so it fits both themes.

- [ ] **Step 5: SessionPage.** Replace the `<CardTitle><h1>Session {session.id}</h1></CardTitle>` with `<CardTitle><SessionAnnotationHeader session={session} /></CardTitle>`, keep the `sessionKey` line and description. Destructure `note` from `detail.data` and render `<SessionNoteCard sessionId={session.id} note={note} />` directly after the header card.

- [ ] **Step 6: Run and confirm GREEN** — `npm run test -- src/pages/SessionPage.test.tsx`, then `npm run check` and `npm run build`.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Dashboard/ui/src
git commit -m "feat(dashboard-ui): edit session title, star, archive and note on the session page"
```

---

### Task 6: Documentation, gates and plan row

**Files:**
- Modify: `README.md` (Activity journal section; dashboard section), `plans/README.md`, `docs/superpowers/specs/2026-10-14-dashboard-session-curation-design.md` (status line only)

- [ ] **Step 1: README.** In the Activity journal section add a short paragraph: the journal also holds operator annotations of sessions (title, note, star, archive) written only from the dashboard; retention deletes them with their session; a star does not protect a session. In the dashboard section describe: search (title, note, directory, session key, client title), Active/Archived, starred sessions pinned at the top, inline title edit and note on the session page. Do not change `Capabilities.cs` or `skills/sqlharness/SKILL.md` (not agent-facing). Run `rg -n "two guarded writes|only write" README.md docs src/SqlHarness.Dashboard` and update any statement that the dashboard has exactly two writes or never writes the journal.

- [ ] **Step 2: Gates.** Foreground, one at a time: `pwsh ./scripts/verify.ps1`; after it finishes, `pwsh ./scripts/verify-linux.ps1`. Record exit codes and the literal pass line of each; search each log case-sensitively for `FAILED`. A partial log is not a pass.

- [ ] **Step 3: Plan row and spec status.** In `plans/README.md` set the 069 row to DONE with the commit range and both gate results. In the spec set `Status:` to "implemented by Plan 069".

- [ ] **Step 4: Commit**

```bash
git add README.md plans/README.md docs/superpowers/specs/2026-10-14-dashboard-session-curation-design.md
git commit -m "docs: session curation in the dashboard and journal (plan 069)"
```

Integration into `main` and any push only on a separate operator instruction.
