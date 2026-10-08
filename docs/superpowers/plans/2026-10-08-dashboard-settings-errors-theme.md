# Dashboard settings, profiles, error details, SQL rendering and theme — Implementation Plan

**Status (2026-10-08):** DONE — e897844 (error details), 01eba6a (settings), e5135c1 (profiles), 73dc28a (theme), 970bca1 (SQL highlighting), 4d35679/ece2f30 (docs). Checkboxes below were not maintained during execution.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the operator edit `config.json` settings, view profiles read-only, see why an operation failed or was rejected, read formatted and highlighted SQL, and pick a light/dark/system theme in the SQLHarness dashboard.

**Architecture:** The journal gains an `error_message` column (schema v4) that is written only with `journal.storeSensitive`. The dashboard API gains `GET/PUT /api/settings` (the only write endpoint, guarded by a custom header, `Origin`, and JSON content type on top of the existing cookie and `Host` checks) and `GET /api/profiles`. The React SPA adds a status-badge tooltip and dialog, a `SqlBlock` component (`sql-formatter` + `highlight.js`), a theme menu, and Settings and Profiles pages.

**Tech Stack:** .NET (C#, ASP.NET Core minimal API, Microsoft.Data.Sqlite, xUnit), React 19 + TypeScript + Vite, TanStack Router/Query, shadcn/ui on Base UI, Tailwind v4, Vitest + Testing Library.

**Spec:** `docs/superpowers/specs/2026-10-07-dashboard-settings-errors-theme-design.md`

## Global Constraints

- Do not auto-format. Never run `dotnet format` to rewrite files; touch only the lines a change needs and match local style. (`verify.ps1` runs `dotnet format --verify-no-changes`, which only checks.)
- A change is done only when both `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1` are green.
- Commit titles: one short line, no task key (this repo has none); end each commit body with the session attribution lines the harness provides.
- Error messages are stored only with `journal.storeSensitive: true`; truncated to 4096 characters with a trailing `…`.
- `PUT /api/settings` is the only non-GET/HEAD route. It requires header `X-SqlHarness-Dashboard: 1`, `Origin` absent or equal to `http://127.0.0.1:<port>` / `http://localhost:<port>`, and a JSON content type; failures return `403`. Any other non-GET method or route returns `405` with `Allow: GET, HEAD`.
- `dashboard.port` is never changed by `PUT /api/settings`; it is taken from the current file (or the default 47800).
- An invalid `config.json` is overwritten only with `?overwriteInvalid=true`; otherwise `409`.
- PUT body at most 64 KiB (`SqlHarnessConfigLoader.MaximumBytes`).
- Profiles: never read environment variables; return the `passwordEnvVar` name only.
- Theme storage key: `sqlharness.theme`; values `system` | `light` | `dark`; every `localStorage` access in `try/catch`.
- SQL dialects: `postgresql` for engine `postgres`, `transactsql` otherwise.
- shadcn primitives are added with `npx shadcn@latest add <name>` from `src/SqlHarness.Dashboard/ui` and stay unchanged; if the CLI rewrites `src/index.css` to `@import "shadcn/tailwind.css"`, point it back at `./styles/shadcn-tailwind.css`.

## Review Focus

1. **A PUT carrying a valid cookie but sent from a foreign page (cross-site form or fetch).** Expect `403`: forms cannot set the custom header and a foreign `Origin` fails. Test lives in Task 4 (`Put_from_foreign_origin_or_without_header_is_forbidden`).
2. **A PUT with `?t=<token>` and no cookie.** Expect it to be rejected (`403`), never to set a cookie or redirect. Test in Task 4 (`Put_never_accepts_the_token_exchange`).
3. **The config file being replaced while a CLI process reads it.** Expect readers to see either the old or the new complete file, never a partial one: the write goes to a temp file in the same directory and is moved over the target. Test in Task 3 (`Write_replaces_atomically_and_leaves_no_temp_file`).
4. **An error message containing HTML or script (e.g. a SQL Server message quoting `<script>`).** Expect it to render as text in the dialog. Test in Task 7 (`renders error message as text`). The same for SQL: `highlight.js` escapes input; test in Task 9 (`escapes html in sql`).
5. **A profile whose password environment variable is set in the dashboard's process.** Expect the value never to appear in `/api/profiles`. Test in Task 5 (`Profiles_never_contain_password_values`).

---

## File structure

**Core (`src/SqlHarness.Core`)**
- Modify `Journal/JournalSchema.cs` — `CurrentVersion = 4`, `Version4`.
- Modify `Journal/JournalModels.cs` — `OperationEnd.ErrorMessage`.
- Modify `Journal/ActivityJournal.cs` — migration step, gated write, truncation.
- Modify `Journal/OperationJournalDescriber.cs` — fill `ErrorMessage`.
- Modify `SqlHarnessConfig.cs` — `SqlHarnessConfigLoader.Parse` with field errors.
- Create `SqlHarnessConfigWriter.cs` — atomic, owner-only write.

**Dashboard (`src/SqlHarness.Dashboard`)**
- Modify `DashboardModels.cs` — `OperationSummary.ErrorMessage`.
- Modify `JournalReader.cs` — select `o.error_message`.
- Modify `DashboardSecurity.cs` — allow the guarded settings PUT.
- Create `DashboardSettings.cs` — GET/PUT handlers.
- Create `DashboardProfiles.cs` — read-only profile view.
- Modify `DashboardServer.cs` — `ConfigPath`, `TargetsPath`, routes.
- Modify `DashboardHost.cs` — `ConfigPath`, pass paths, reload config before retention.
- Modify `src/SqlHarness.Cli/Commands/DashboardCommand.cs` — set `ConfigPath`.

**UI (`src/SqlHarness.Dashboard/ui/src`)**
- Create `components/ui/{dialog,tooltip,switch,input,label,dropdown-menu}.tsx` (shadcn CLI).
- Create `lib/errors.ts`, `lib/sql.ts`, `components/SqlBlock.tsx`, `components/ThemeMenu.tsx`, `pages/SettingsPage.tsx`, `pages/ProfilesPage.tsx`.
- Modify `lib/theme.ts`, `main.tsx`, `components/AppLayout.tsx`, `components/StatusBadge.tsx`, `components/OperationTable.tsx`, `pages/OperationPage.tsx`, `components/PlanTree.tsx`, `components/VariantPanel.tsx`, `api/types.ts`, `api/client.ts`, `api/queries.ts`, `router.tsx`, `index.css`, `test/fixtures.ts`, `test/render.tsx`.

**Tests**
- `tests/SqlHarness.Tests/Journal/{ActivityJournalTests,OperationJournalDescriberTests,SqlHarnessConfigTests}.cs`
- `tests/SqlHarness.Tests/Dashboard/{DashboardContractTests,JournalReaderTests,DashboardServerTests,DashboardHousekeepingTests}.cs`
- Create `tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs`, `DashboardProfilesTests.cs`.
- UI tests next to the files they cover (`*.test.ts[x]`).

**Docs**
- `AGENTS.md`, `README.md` (dashboard section), `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md` (pointer note).

Build/test commands (no Makefile in this repo; use raw commands):
- One .NET test class: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~<Class>"`
- UI: `npm --prefix src/SqlHarness.Dashboard/ui run test -- <file-pattern>`; typecheck/lint/test: `npm --prefix src/SqlHarness.Dashboard/ui run check`

---

### Task 1: Journal stores error messages (schema v4)

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs`
- Modify: `src/SqlHarness.Core/Journal/JournalModels.cs:39-51`
- Modify: `src/SqlHarness.Core/Journal/ActivityJournal.cs` (`Complete` ~line 107, `Migrate` ~line 280)
- Modify: `src/SqlHarness.Core/Journal/OperationJournalDescriber.cs`
- Test: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, `tests/SqlHarness.Tests/Journal/OperationJournalDescriberTests.cs`

**Interfaces:**
- Produces: `OperationEnd(..., string? SummaryJson = null, string? ErrorMessage = null)`; column `operations.error_message TEXT`; `JournalSchema.CurrentVersion == 4`; `ActivityJournal.ErrorMessageLimit == 4096`; `ActivityJournal.TruncateErrorMessage(string?) : string?` (internal static).

- [ ] **Step 1: Write the failing journal tests** (append to `ActivityJournalTests`)

```csharp
    [Fact]
    public void Error_message_is_stored_only_when_store_sensitive()
    {
        using var hashOnly = new JournalTempDirectory();
        using var sensitive = new JournalTempDirectory();
        var end = JournalTestData.End("failed", 5) with { ErrorKind = "sql_execution_failed", ErrorMessage = "Invalid object name 'SQLH_ERR_MARKER'." };

        var plain = Open(hashOnly, TextWriter.Null);
        plain.Complete(plain.Begin(JournalTestData.Session(), JournalTestData.Start()), end);
        var stored = Open(sensitive, TextWriter.Null, storeSensitive: true);
        stored.Complete(stored.Begin(JournalTestData.Session(), JournalTestData.Start()), end);

        Assert.False(JournalDb.Contains(JournalDb.AllBytes(hashOnly.DatabasePath), "SQLH_ERR_MARKER"));
        Assert.Null(JournalDb.Rows(hashOnly.DatabasePath, "SELECT error_message FROM operations")[0]["error_message"]);
        Assert.Equal("Invalid object name 'SQLH_ERR_MARKER'.",
            JournalDb.Rows(sensitive.DatabasePath, "SELECT error_message FROM operations")[0]["error_message"]);
    }

    [Fact]
    public void Long_error_message_is_truncated()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null, storeSensitive: true);
        var end = JournalTestData.End("failed", 5) with { ErrorMessage = new string('x', 5000) };

        journal.Complete(journal.Begin(JournalTestData.Session(), JournalTestData.Start()), end);

        var message = (string)JournalDb.Rows(temp.DatabasePath, "SELECT error_message FROM operations")[0]["error_message"]!;
        Assert.Equal(ActivityJournal.ErrorMessageLimit, message.Length);
        Assert.EndsWith("…", message);
    }

    [Fact]
    public void Version_3_journal_migrates_to_4_and_keeps_rows()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = JournalSchema.Version1 + JournalSchema.Version2 + JournalSchema.Version3 + """
                INSERT INTO sessions (session_key, agent_kind, transport, source, host_pid, first_seen, last_seen)
                VALUES ('cli:old', 'claude', 'cli', 'process-tree', 1, 't', 't');
                INSERT INTO operations (session_id, operation, host_pid, started_at, updated_at, status, error_kind)
                VALUES (1, 'query', 1, 't', 't', 'failed', 'sql_execution_failed');
                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }

        Open(temp, TextWriter.Null);

        Assert.Equal(4L, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        var row = JournalDb.Rows(temp.DatabasePath, "SELECT error_kind, error_message FROM operations")[0];
        Assert.Equal("sql_execution_failed", row["error_kind"]);
        Assert.Null(row["error_message"]);
    }
```

Also update the existing `Open_creates_current_schema_in_wal_mode` test if it asserts `3` literally (it should use `JournalSchema.CurrentVersion`; change a literal `3L` to `(long)JournalSchema.CurrentVersion`).

- [ ] **Step 2: Write the failing describer tests** (replace `Failure_end_carries_machine_error_code_only` in `OperationJournalDescriberTests`)

```csharp
    [Fact]
    public void Failure_end_carries_machine_error_code_and_message()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "SQL safety rejection: SELECT secret");

        var end = OperationJournalDescriber.DescribeEnd(outcome, 1);

        Assert.Equal("rejected", end.Status);
        Assert.Equal(2, end.ExitCode);
        Assert.Equal(outcome.MachineError?.Code, end.ErrorKind);
        Assert.Equal("SQL safety rejection: SELECT secret", end.ErrorMessage);
        Assert.Null(end.Server);
    }

    [Fact]
    public void Error_hint_is_appended_on_a_new_line()
    {
        var error = new SqlHarnessError("sql_execution_failed", "sql", "Timeout expired.", Hint: "Raise --timeout.");
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, null, Error: error);

        Assert.Equal("Timeout expired.\nRaise --timeout.", OperationJournalDescriber.DescribeEnd(outcome, 1).ErrorMessage);
    }

    [Fact]
    public void Success_and_controlled_outcomes_carry_no_error_message()
    {
        Assert.Null(OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null), 1).ErrorMessage);
        Assert.Null(OperationJournalDescriber.DescribeEnd(new SqlHarnessOutcome(SqlHarnessExitCode.WatchMaxDuration, null, null), 1).ErrorMessage);
        Assert.Null(OperationJournalDescriber.Cancelled(1).ErrorMessage);
        Assert.Null(OperationJournalDescriber.Crashed(1).ErrorMessage);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ActivityJournalTests|FullyQualifiedName~OperationJournalDescriberTests"`
Expected: compile errors (`ErrorMessage` / `ErrorMessageLimit` do not exist).

- [ ] **Step 4: Implement**

`JournalSchema.cs`: change `CurrentVersion = 3` to `4` and add after `Version3`:

```csharp
    internal const string Version4 = """
        ALTER TABLE operations ADD COLUMN error_message TEXT;
        """;
```

`ActivityJournal.Migrate`, after `if (locked < 3) ...`:

```csharp
            if (locked < 4)
                Execute(connection, JournalSchema.Version4);
```

`JournalModels.cs` — add the last parameter to `OperationEnd`:

```csharp
    string? SummaryJson = null,
    string? ErrorMessage = null);
```

`ActivityJournal.Complete`: add `error_message = $message,` after `error_kind = $error,` in the `UPDATE` and the parameter:

```csharp
            update.Parameters.AddWithValue("$message", _storeSensitive ? (object?)TruncateErrorMessage(end.ErrorMessage) ?? DBNull.Value : DBNull.Value);
```

and in the class body:

```csharp
    /// <summary>Longest stored error message, including the trailing ellipsis of a truncated one.</summary>
    internal const int ErrorMessageLimit = 4096;

    internal static string? TruncateErrorMessage(string? message) =>
        message is null || message.Length <= ErrorMessageLimit ? message : message[..(ErrorMessageLimit - 1)] + "…";
```

`OperationJournalDescriber`: update the class comment's last sentence to "...and target identity, plus the error message for opt-in storage; it never reads parameter values or result rows." In `DescribeEnd`, compute `var status = Status(outcome.ExitCode);`, pass `status` as the first argument, and add:

```csharp
            SummaryJson: JournalSummary.Build(outcome.Report),
            ErrorMessage: status is "failed" or "rejected" ? Message(outcome.MachineError) : null);
```

with

```csharp
    private static string? Message(SqlHarnessError? error) =>
        error is null ? null : string.IsNullOrWhiteSpace(error.Hint) ? error.Message : error.Message + "\n" + error.Hint;
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`
Expected: PASS (this also runs `JournalGainStoreTests` with its v2/v3 shapes and the dashboard schema checks).

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Core/Journal tests/SqlHarness.Tests/Journal
git commit -m "Store operation error messages in the journal when storeSensitive is on"
```

---

### Task 2: Dashboard API exposes `errorMessage`

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardModels.cs:22-26`
- Modify: `src/SqlHarness.Dashboard/JournalReader.cs:28-38` (`OperationColumns`), `:436-442` (`ReadOperation`)
- Modify: `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `ui/src/test/fixtures.ts`
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs`, `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`

**Interfaces:**
- Consumes: column `operations.error_message` (Task 1).
- Produces: `OperationSummary(..., JsonElement? Progress, string? ErrorMessage)`; TS `OperationSummary.errorMessage: string | null`.

- [ ] **Step 1: Update the contract test** — in `Operation_summary_names_match_the_ui` add a trailing `null` argument to the constructor call and `"errorMessage"` after `"progress"` in the expected array.

- [ ] **Step 2: Write the failing reader test** (append to `JournalReaderTests`; it seeds through the real journal so the gate from Task 1 is exercised)

```csharp
    [Fact]
    public void Operation_carries_the_stored_error_message()
    {
        using var home = new TempHome();
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null, TimeProvider.System);
        var handle = journal.Begin(Journal.JournalTestData.Session(), Journal.JournalTestData.Start());
        journal.Complete(handle, Journal.JournalTestData.End("failed", 5) with { ErrorKind = "sql_execution_failed", ErrorMessage = "Invalid column name 'x'." });
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        var operation = reader.Operation(handle!.OperationId)!.Operation;

        Assert.Equal("sql_execution_failed", operation.ErrorKind);
        Assert.Equal("Invalid column name 'x'.", operation.ErrorMessage);
    }
```

(Add `using SqlHarness.Core;` if the file lacks it.)

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardContractTests|FullyQualifiedName~JournalReaderTests"`
Expected: compile error (`ErrorMessage` missing on `OperationSummary`).

- [ ] **Step 4: Implement**

`DashboardModels.cs`: append `, string? ErrorMessage` after `JsonElement? Progress` in `OperationSummary`.

`JournalReader.OperationColumns`: append `, o.error_message` after the `over_granted` expression (it becomes column index 25):

```csharp
                AND m.grant_max_used_kb IS NOT NULL AND m.grant_max_used_kb * 4 < m.grant_granted_kb) AS over_granted,
        o.error_message
        """;
```

`ReadOperation`: append `, NullableString(r, 25)` after `Json(NullableString(r, 18))`.

Fix any other `new OperationSummary(` call sites the compiler reports (tests/seeds) by adding a trailing `null`.

`ui/src/api/types.ts` — in `OperationSummary` after `progress`:

```ts
  progress: WatchProgress | null
  /** Full error text; stored only with journal.storeSensitive. */
  errorMessage: string | null
```

`ui/src/test/fixtures.ts` — in the `operation` factory defaults add `errorMessage: null,`.

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"` and `npm --prefix src/SqlHarness.Dashboard/ui run typecheck`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard
git commit -m "Expose operation error messages in the dashboard API"
```

---

### Task 3: Config parsing with field errors and an atomic writer

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessConfig.cs` (`SqlHarnessConfigLoader`)
- Create: `src/SqlHarness.Core/SqlHarnessConfigWriter.cs`
- Test: `tests/SqlHarness.Tests/Journal/SqlHarnessConfigTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SqlHarnessConfigFieldError(string Field, string Message);`
  - `public sealed record SqlHarnessConfigParseResult(SqlHarnessConfig? Config, IReadOnlyList<SqlHarnessConfigFieldError> Errors);`
  - `public static SqlHarnessConfigParseResult SqlHarnessConfigLoader.Parse(byte[] json)` — strict; `Config` non-null iff `Errors` empty.
  - `public static void SqlHarnessConfigWriter.Write(string path, SqlHarnessConfig config)` — throws `IOException`/`UnauthorizedAccessException` on failure.
  - `public static string SqlHarnessConfigWriter.Serialize(SqlHarnessConfig config)` — indented camelCase JSON.

- [ ] **Step 1: Write the failing tests** (append to `SqlHarnessConfigTests`)

```csharp
    [Fact]
    public void Parse_reports_out_of_range_fields_by_path()
    {
        var result = SqlHarnessConfigLoader.Parse("""
            { "journal": { "enabled": true, "storeSensitive": false,
                           "retention": { "enabled": true, "maxAgeDays": 0, "maxSizeMb": 5 } },
              "dashboard": { "autoStart": false, "port": 47800, "idleShutdownHours": 999 } }
            """u8.ToArray());

        Assert.Null(result.Config);
        Assert.Equal(
            ["journal.retention.maxAgeDays", "journal.retention.maxSizeMb", "dashboard.idleShutdownHours"],
            result.Errors.Select(error => error.Field));
    }

    [Fact]
    public void Parse_rejects_unknown_fields_and_wrong_types()
    {
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("""{ "journal": { "bogus": 1 } }"""u8.ToArray()).Errors);
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("""{ "journal": { "enabled": "yes" } }"""u8.ToArray()).Errors);
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("""{ "journal": null }"""u8.ToArray()).Errors);
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("not json"u8.ToArray()).Errors);
    }

    [Fact]
    public void Written_config_round_trips_through_the_loader()
    {
        var config = SqlHarnessConfig.Default with
        {
            Journal = new JournalConfig { StoreSensitive = true, Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 7 } },
            Dashboard = new DashboardConfig { AutoStart = true, IdleShutdownHours = 2 },
        };

        SqlHarnessConfigWriter.Write(ConfigPath, config);
        var loaded = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Valid, loaded.Status);
        Assert.Equal(config, loaded.Config);
    }

    [Fact]
    public void Write_replaces_atomically_and_leaves_no_temp_file()
    {
        File.WriteAllText(ConfigPath, "{ broken");

        SqlHarnessConfigWriter.Write(ConfigPath, SqlHarnessConfig.Default);

        Assert.Equal(SqlHarnessConfigStatus.Valid, SqlHarnessConfigLoader.Load(ConfigPath).Status);
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ConfigPath));
    }

    [Fact]
    public void Write_creates_the_home_directory()
    {
        var nested = Path.Combine(_dir, "fresh", "config.json");

        SqlHarnessConfigWriter.Write(nested, SqlHarnessConfig.Default);

        Assert.True(File.Exists(nested));
    }
```

Note: `SqlHarnessConfig` records hold nested records, so `Assert.Equal(config, loaded.Config)` compares by value.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlHarnessConfigTests"`
Expected: compile errors (`Parse`, `SqlHarnessConfigWriter` missing).

- [ ] **Step 3: Implement `Parse` and reuse it in `Load`**

In `SqlHarnessConfig.cs`, add the two records next to `SqlHarnessConfigLoadResult`:

```csharp
public sealed record SqlHarnessConfigFieldError(string Field, string Message);

public sealed record SqlHarnessConfigParseResult(SqlHarnessConfig? Config, IReadOnlyList<SqlHarnessConfigFieldError> Errors);
```

In `SqlHarnessConfigLoader`, replace the body of the `try` in `Load(string path)` after the size check with:

```csharp
            var parsed = Parse(File.ReadAllBytes(path));
            return parsed.Config is { } config
                ? new SqlHarnessConfigLoadResult(config, SqlHarnessConfigStatus.Valid, null)
                : Invalid();
```

and add (replacing `IsInRange`, keeping `IsComplete`):

```csharp
    /// <summary>Strict parse of one config document; the config is returned only when there are no errors.</summary>
    public static SqlHarnessConfigParseResult Parse(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumBytes)
            return Failed("$", "The settings document is larger than 64 KiB.");
        SqlHarnessConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<SqlHarnessConfig>(json, Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            var path = (exception as JsonException)?.Path;
            return Failed(string.IsNullOrEmpty(path) ? "$" : path.TrimStart('$', '.'), "Unknown field or invalid value.");
        }

        if (config is null || !IsComplete(config))
            return Failed("$", "The journal, journal.retention and dashboard sections must be objects.");

        var errors = new List<SqlHarnessConfigFieldError>();
        if (config.Journal.Retention.MaxAgeDays is < 1 or > 3650)
            errors.Add(new("journal.retention.maxAgeDays", "Must be between 1 and 3650."));
        if (config.Journal.Retention.MaxSizeMb is < 10 or > 102_400)
            errors.Add(new("journal.retention.maxSizeMb", "Must be between 10 and 102400."));
        if (config.Dashboard.Port is < 1024 or > 65535)
            errors.Add(new("dashboard.port", "Must be between 1024 and 65535."));
        if (config.Dashboard.IdleShutdownHours is < 1 or > 168)
            errors.Add(new("dashboard.idleShutdownHours", "Must be between 1 and 168."));
        return errors.Count == 0 ? new(config, []) : new(null, errors);
    }

    private static SqlHarnessConfigParseResult Failed(string field, string message) => new(null, [new(field, message)]);
```

- [ ] **Step 4: Create `SqlHarnessConfigWriter.cs`**

```csharp
using System.Text.Json;

namespace SqlHarness.Core;

/// <summary>
/// Writes <c>config.json</c> so readers see either the old or the new complete file:
/// a temp file in the same directory is written, made owner-only, then moved over the target.
/// </summary>
public static class SqlHarnessConfigWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Serialize(SqlHarnessConfig config) => JsonSerializer.Serialize(config, Options);

    public static void Write(string path, SqlHarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".config-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, Serialize(config) + Environment.NewLine);
            OwnerOnlyFiles.File(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
```

(`OwnerOnlyFiles` is internal in `SqlHarness.Core/Journal/OwnerOnlyFiles.cs`, same assembly; check its namespace and add a `using` if it differs.)

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlHarnessConfigTests"`
Expected: PASS, including the pre-existing loader tests.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Core/SqlHarnessConfig.cs src/SqlHarness.Core/SqlHarnessConfigWriter.cs tests/SqlHarness.Tests/Journal/SqlHarnessConfigTests.cs
git commit -m "Add strict config parsing with field errors and an atomic config writer"
```

---

### Task 4: Settings API and the guarded write path

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardSecurity.cs` (`GuardAsync`)
- Create: `src/SqlHarness.Dashboard/DashboardSettings.cs`
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs` (options, routes)
- Test: create `tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs`; modify `DashboardServerTests.cs` (`StartAuthenticated`, `Only_get_endpoints_exist`)

**Interfaces:**
- Consumes: `SqlHarnessConfigLoader.Load/Parse`, `SqlHarnessConfigWriter.Write` (Task 3).
- Produces:
  - `DashboardServerOptions.ConfigPath { get; init; }` (default `SqlHarnessPaths.ConfigFile`), `DashboardServerOptions.TargetsPath { get; init; }` (default `SqlHarnessPaths.TargetsFile`).
  - `DashboardSecurity.WriteHeader == "X-SqlHarness-Dashboard"`.
  - `public sealed record SettingsResponse(string Status, string Path, SqlHarnessConfig Settings);` JSON: `{ status, path, settings: { journal: {...}, dashboard: {...} } }`, status `missing|valid|invalid`.
  - `400` body `{ errors: [{ field, message }] }`; `409`/`500`/`403` body `{ error }` (403 is plain text from `Reject`).

- [ ] **Step 1: Point the test server at temp files** — in `DashboardServerTests.StartAuthenticated`:

```csharp
        var server = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses())
            {
                ConfigPath = Path.Combine(home.Path, "config.json"),
                TargetsPath = Path.Combine(home.Path, "targets.json"),
            },
            CancellationToken.None);
```

- [ ] **Step 2: Update the route-table test** — rename `Only_get_endpoints_exist` to `Only_get_endpoints_and_the_settings_put_exist` and replace the `Assert.All` line:

```csharp
        var writes = server.Endpoints.Where(endpoint => !endpoint.Methods.SequenceEqual(["GET", "HEAD"])).ToArray();
        var settingsPut = Assert.Single(writes);
        Assert.Equal(("/api/settings", (IReadOnlyList<string>)["PUT"]), (settingsPut.Route, settingsPut.Methods));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsync("/api/sessions", null)).StatusCode);
```

(If `RoutePattern.RawText` for a grouped route renders as `/api/settings`, as for `/api/live`, the assertion matches; adjust only the string form if the existing `/api/live` assertion shows a different shape.)

- [ ] **Step 3: Write the failing settings tests** — create `DashboardSettingsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardSettingsTests
{
    private const string ValidBody = """
        { "journal": { "enabled": true, "storeSensitive": true,
                       "retention": { "enabled": true, "maxAgeDays": 7, "maxSizeMb": 100 } },
          "dashboard": { "autoStart": true, "port": 50000, "idleShutdownHours": 2 } }
        """;

    private static string ConfigPath(TempHome home) => Path.Combine(home.Path, "config.json");

    private static HttpRequestMessage Put(RunningDashboard server, string body, string path = "/api/settings",
        bool header = true, string? origin = null, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (header)
            request.Headers.Add(DashboardSecurity.WriteHeader, "1");
        request.Headers.Add("Origin", origin ?? $"http://127.0.0.1:{server.Port}");
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Get_reports_missing_file_with_defaults()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var body = await Json(await client.GetAsync("/api/settings"));

        Assert.Equal("missing", body.GetProperty("status").GetString());
        Assert.Equal(ConfigPath(home), body.GetProperty("path").GetString());
        Assert.False(body.GetProperty("settings").GetProperty("journal").GetProperty("storeSensitive").GetBoolean());
        Assert.Equal(47800, body.GetProperty("settings").GetProperty("dashboard").GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task Get_reports_invalid_file_without_its_contents()
    {
        using var home = new TempHome();
        File.WriteAllText(ConfigPath(home), "{ \"SQLH_SECRET_MARKER\": ");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var text = await client.GetStringAsync("/api/settings");

        Assert.Contains("\"status\":\"invalid\"", text);
        Assert.DoesNotContain("SQLH_SECRET_MARKER", text);
    }

    [Fact]
    public async Task Put_writes_the_file_and_keeps_the_current_port()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        var response = await client.SendAsync(Put(server, ValidBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var loaded = SqlHarnessConfigLoader.Load(ConfigPath(home));
        Assert.Equal(SqlHarnessConfigStatus.Valid, loaded.Status);
        Assert.True(loaded.Config.Journal.StoreSensitive);
        Assert.Equal(7, loaded.Config.Journal.Retention.MaxAgeDays);
        Assert.Equal(47800, loaded.Config.Dashboard.Port);
        Assert.Equal("valid", (await Json(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Put_with_invalid_body_returns_field_errors_and_leaves_the_file()
    {
        using var home = new TempHome();
        File.WriteAllText(ConfigPath(home), "{}");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        var response = await client.SendAsync(Put(server, ValidBody.Replace("\"maxAgeDays\": 7", "\"maxAgeDays\": 0")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = Assert.Single((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal("journal.retention.maxAgeDays", error.GetProperty("field").GetString());
        Assert.Equal("{}", File.ReadAllText(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_over_an_invalid_file_requires_explicit_overwrite()
    {
        using var home = new TempHome();
        File.WriteAllText(ConfigPath(home), "{ broken");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Put(server, ValidBody))).StatusCode);
        Assert.Equal("{ broken", File.ReadAllText(ConfigPath(home)));

        var forced = await client.SendAsync(Put(server, ValidBody, "/api/settings?overwriteInvalid=true"));
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        Assert.Equal(SqlHarnessConfigStatus.Valid, SqlHarnessConfigLoader.Load(ConfigPath(home)).Status);
    }

    [Fact]
    public async Task Put_from_foreign_origin_or_without_header_is_forbidden()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Put(server, ValidBody, header: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Put(server, ValidBody, origin: "http://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Put(server, ValidBody, contentType: "text/plain"))).StatusCode);
        Assert.False(File.Exists(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_without_cookie_is_unauthorized()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, _) = dashboard;
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Put(server, ValidBody))).StatusCode);
        Assert.False(File.Exists(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_never_accepts_the_token_exchange()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, _) = dashboard;
        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseUri };

        var response = await anonymous.SendAsync(Put(server, ValidBody, $"/api/settings?t={server.Token}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(File.Exists(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_without_origin_header_is_allowed()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        var request = Put(server, ValidBody);
        request.Headers.Remove("Origin");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardSettingsTests|FullyQualifiedName~DashboardServerTests"`
Expected: compile errors (`ConfigPath`, `WriteHeader` missing).

- [ ] **Step 5: Implement the security change** — in `DashboardSecurity`:

```csharp
    internal const string WriteHeader = "X-SqlHarness-Dashboard";
    internal const string SettingsPath = "/api/settings";
```

Replace the method check in `GuardAsync` with:

```csharp
        var isRead = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var isSettingsWrite = HttpMethods.IsPut(context.Request.Method)
            && string.Equals(context.Request.Path.Value, SettingsPath, StringComparison.Ordinal);
        if (!isRead && !isSettingsWrite)
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await Reject(context, StatusCodes.Status405MethodNotAllowed, "Method not allowed.");
            return;
        }

        // The one write: a cross-site form cannot set the custom header, a cross-site fetch with it
        // needs a CORS preflight the dashboard never answers, and a foreign Origin is refused outright.
        if (isSettingsWrite && !IsTrustedWrite(context.Request, allowedPort))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "Forbidden.");
            return;
        }
```

and add:

```csharp
    private static bool IsTrustedWrite(HttpRequest request, int port)
    {
        if (request.Query.ContainsKey(TokenQuery))
            return false;
        if (!string.Equals(request.Headers[WriteHeader].ToString(), "1", StringComparison.Ordinal))
            return false;
        if (!request.HasJsonContentType())
            return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0
            || string.Equals(origin, $"http://127.0.0.1:{port}", StringComparison.Ordinal)
            || string.Equals(origin, $"http://localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }
```

(`HasJsonContentType` is in `Microsoft.AspNetCore.Http`; it is an extension on `HttpRequest`.) Update the class summary: "GET-only except one guarded PUT on /api/settings".

- [ ] **Step 6: Create `DashboardSettings.cs`**

```csharp
using Microsoft.AspNetCore.Http;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record SettingsResponse(string Status, string Path, SqlHarnessConfig Settings);

/// <summary>
/// Reads and replaces operator settings in config.json. The file is validated with the
/// loader's strict rules, dashboard.port is always kept from the current file, and an
/// invalid file is replaced only on explicit request. Raw file text never leaves the server.
/// </summary>
internal static class DashboardSettings
{
    internal static SettingsResponse Read(string path)
    {
        var loaded = SqlHarnessConfigLoader.Load(path);
        return new SettingsResponse(loaded.Status.ToString().ToLowerInvariant(), path, loaded.Config);
    }

    internal static async Task<IResult> PutAsync(HttpContext context, string path, System.Text.Json.JsonSerializerOptions json)
    {
        var body = await ReadBodyAsync(context.Request, context.RequestAborted);
        if (body is null)
            return Results.Json(new { errors = new[] { new SqlHarnessConfigFieldError("$", "The settings document is larger than 64 KiB.") } }, json, statusCode: StatusCodes.Status400BadRequest);
        var parsed = SqlHarnessConfigLoader.Parse(body);
        if (parsed.Config is not { } requested)
            return Results.Json(new { errors = parsed.Errors }, json, statusCode: StatusCodes.Status400BadRequest);

        var current = SqlHarnessConfigLoader.Load(path);
        var overwrite = string.Equals(context.Request.Query["overwriteInvalid"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        if (current.Status == SqlHarnessConfigStatus.Invalid && !overwrite)
            return Results.Json(new { error = "config.json is invalid; confirm replacing it." }, json, statusCode: StatusCodes.Status409Conflict);

        // The running dashboard owns its port; changing it from the page would cut the page off.
        var config = requested with { Dashboard = requested.Dashboard with { Port = current.Config.Dashboard.Port } };
        try
        {
            SqlHarnessConfigWriter.Write(path, config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Json(new { error = "Could not write config.json." }, json, statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Json(Read(path), json);
    }

    /// <summary>The request body, or null when it exceeds the config size limit.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > SqlHarnessConfigLoader.MaximumBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
```

(`SqlHarnessConfigLoader.MaximumBytes` is `internal`; Core already has `InternalsVisibleTo("SqlHarness.Dashboard")`.)

- [ ] **Step 7: Wire options and routes in `DashboardServer.cs`**

In `DashboardServerOptions` add:

```csharp
    /// <summary>The operator settings file the settings page reads and writes.</summary>
    public string ConfigPath { get; init; } = SqlHarnessPaths.ConfigFile;

    /// <summary>The closed profile file shown read-only on the profiles page.</summary>
    public string TargetsPath { get; init; } = SqlHarnessPaths.TargetsFile;
```

In `Build`, before `MapRead(api, "/{**rest}", ...)`:

```csharp
        MapRead(api, "/settings", () => Results.Json(DashboardSettings.Read(options.ConfigPath), Json));
        // The only write route; DashboardSecurity admits PUT here alone, with its extra checks.
        api.MapMethods("/settings", [HttpMethods.Put], (HttpContext context) => DashboardSettings.PutAsync(context, options.ConfigPath, Json));
```

- [ ] **Step 8: Run tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard
git commit -m "Add a guarded settings endpoint to the dashboard API"
```

---

### Task 5: Profiles endpoint (read-only)

**Files:**
- Create: `src/SqlHarness.Dashboard/DashboardProfiles.cs`
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs` (route)
- Test: create `tests/SqlHarness.Tests/Dashboard/DashboardProfilesTests.cs`

**Interfaces:**
- Consumes: `ProfileStore.Load(string? path)` (throws `SqlHarnessSafetyException` on an invalid file), `DashboardServerOptions.TargetsPath` (Task 4).
- Produces JSON for `GET /api/profiles`:
  - `public sealed record ProfileVariable(string Name, string Rule);`
  - `public sealed record ProfileView(string Name, string Engine, string Server, string Database, string Auth, string? SqlUser, string? PasswordEnvVar, string? SslMode, bool TrustServerCertificate, string? RootCertificate, IReadOnlyList<ProfileVariable> Vars);`
  - `public sealed record ProfilesResponse(string Status, IReadOnlyList<ProfileView> Profiles, string? Message);` status `missing|valid|invalid`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardProfilesTests
{
    private static string TargetsPath(TempHome home) => Path.Combine(home.Path, "targets.json");

    [Fact]
    public async Task Profiles_list_names_templates_and_auth_metadata()
    {
        using var home = new TempHome();
        File.WriteAllText(TargetsPath(home), """
            {
              "zeta": { "server": "localhost,1433", "database": "db", "auth": "integrated", "vars": {} },
              "pg": { "engine": "postgres", "server": "pg.example", "database": "app_{env}", "auth": "sql",
                      "sqlUser": "reader", "passwordEnvVar": "SQLH_TEST_PG_PASSWORD", "sslMode": "verify-full",
                      "vars": { "env": "uat|test" } }
            }
            """);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var body = JsonDocument.Parse(await client.GetStringAsync("/api/profiles")).RootElement;

        Assert.Equal("valid", body.GetProperty("status").GetString());
        var profiles = body.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Equal(["pg", "zeta"], profiles.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal("postgres", profiles[0].GetProperty("engine").GetString());
        Assert.Equal("app_{env}", profiles[0].GetProperty("database").GetString());
        Assert.Equal("SQLH_TEST_PG_PASSWORD", profiles[0].GetProperty("passwordEnvVar").GetString());
        Assert.Equal("env", profiles[0].GetProperty("vars")[0].GetProperty("name").GetString());
        Assert.Equal("uat|test", profiles[0].GetProperty("vars")[0].GetProperty("rule").GetString());
        Assert.Equal("sqlserver", profiles[1].GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Profiles_never_contain_password_values()
    {
        using var home = new TempHome();
        var variable = "SQLH_TEST_PW_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "SQLH_PASSWORD_VALUE_MARKER");
        try
        {
            File.WriteAllText(TargetsPath(home), $$"""
                { "p": { "server": "s", "database": "d", "auth": "sql", "sqlUser": "u", "passwordEnvVar": "{{variable}}", "vars": {} } }
                """);
            await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
            var (_, client) = dashboard;

            var text = await client.GetStringAsync("/api/profiles");

            Assert.Contains(variable, text);
            Assert.DoesNotContain("SQLH_PASSWORD_VALUE_MARKER", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Missing_and_invalid_files_return_empty_lists_without_file_content()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var missing = JsonDocument.Parse(await client.GetStringAsync("/api/profiles")).RootElement;
        Assert.Equal("missing", missing.GetProperty("status").GetString());
        Assert.Empty(missing.GetProperty("profiles").EnumerateArray());

        File.WriteAllText(TargetsPath(home), "{ \"SQLH_TARGET_MARKER\": ");
        var text = await client.GetStringAsync("/api/profiles");
        Assert.Contains("\"status\":\"invalid\"", text);
        Assert.DoesNotContain("SQLH_TARGET_MARKER", text);
        Assert.DoesNotContain(home.Path.Replace("\\", "\\\\"), text);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardProfilesTests"`
Expected: FAIL — `/api/profiles` returns 404.

- [ ] **Step 3: Implement `DashboardProfiles.cs`**

```csharp
using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Dashboard;

public sealed record ProfileVariable(string Name, string Rule);

public sealed record ProfileView(
    string Name, string Engine, string Server, string Database, string Auth, string? SqlUser, string? PasswordEnvVar,
    string? SslMode, bool TrustServerCertificate, string? RootCertificate, IReadOnlyList<ProfileVariable> Vars);

public sealed record ProfilesResponse(string Status, IReadOnlyList<ProfileView> Profiles, string? Message);

/// <summary>
/// Read-only view of targets.json. It never reads environment variables: a profile's
/// password stays in its variable and only the variable's name is shown.
/// </summary>
internal static class DashboardProfiles
{
    internal const string InvalidMessage = "targets.json could not be read. Run `sqlharness doctor` for details.";

    internal static ProfilesResponse Read(string path)
    {
        if (!File.Exists(path))
            return new ProfilesResponse("missing", [], null);
        IReadOnlyDictionary<string, TargetProfile> profiles;
        try
        {
            profiles = ProfileStore.Load(path);
        }
        catch (Exception exception) when (exception is SqlHarnessSafetyException or IOException or UnauthorizedAccessException)
        {
            // The loader's message names the path; the page gets a fixed sentence instead.
            return new ProfilesResponse("invalid", [], InvalidMessage);
        }

        return new ProfilesResponse("valid", profiles
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => View(pair.Key, pair.Value))
            .ToArray(), null);
    }

    private static ProfileView View(string name, TargetProfile profile) => new(
        name,
        string.IsNullOrWhiteSpace(profile.Engine) ? "sqlserver" : profile.Engine.Trim().ToLowerInvariant(),
        profile.Server,
        profile.Database,
        profile.Auth,
        profile.SqlUser,
        profile.PasswordEnvVar,
        profile.SslMode,
        profile.TrustServerCertificate,
        profile.RootCertificate,
        profile.Vars.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ProfileVariable(pair.Key, pair.Value)).ToArray());
}
```

Route in `DashboardServer.Build`, next to `/settings`:

```csharp
        MapRead(api, "/profiles", () => Results.Json(DashboardProfiles.Read(options.TargetsPath), Json));
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard
git commit -m "Show target profiles read-only in the dashboard API"
```

---

### Task 6: Host passes file paths and reloads config before retention

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardHost.cs` (options record, server start ~line 138, `HousekeepAsync` ~line 197-245)
- Modify: `src/SqlHarness.Cli/Commands/DashboardCommand.cs:47`
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs`

**Interfaces:**
- Consumes: `DashboardServerOptions.ConfigPath/TargetsPath` (Task 4).
- Produces: `DashboardHostOptions.ConfigPath { get; init; }` (`string?`; null keeps the startup `Config` for retention — tests).

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task Retention_uses_settings_saved_after_start()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:old"));
        var configPath = Path.Combine(home.Path, "config.json");
        var clock = new ManualClock(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var passes = new Passes();
        using var cts = new CancellationTokenSource();

        // Started with retention off (defaults); the operator then enables it on the settings page.
        var run = DashboardHost.RunAsync(
            Options(home, idle: null, clock, passes) with { ConfigPath = configPath, RetentionInterval = TimeSpan.FromHours(1) },
            cts.Token);
        await passes.MoreAsync();
        Assert.Single(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));

        SqlHarnessConfigWriter.Write(configPath, SqlHarnessConfig.Default with
        {
            Journal = new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 1 } },
        });
        clock.Advance(TimeSpan.FromHours(1));
        await passes.MoreAsync();
        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));

        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardHousekeepingTests"`
Expected: compile error (`ConfigPath` missing on `DashboardHostOptions`).

- [ ] **Step 3: Implement**

`DashboardHostOptions`:

```csharp
    /// <summary>config.json to re-read before each retention pass; null keeps <see cref="Config"/> (tests).</summary>
    public string? ConfigPath { get; init; }
```

Where the server starts (`new DashboardServerOptions(options.DatabasePath, ...)`), add an initializer:

```csharp
                {
                    LivePollInterval = ...existing...,
                    ConfigPath = options.ConfigPath ?? Path.Combine(options.Home, "config.json"),
                    TargetsPath = Path.Combine(options.Home, "targets.json"),
                }
```

(Merge with whatever initializer already exists there; keep its current properties.)

In `HousekeepAsync`, delete `var journal = options.Config.Config.Journal;` and change the retention block to:

```csharp
                if (lastRetention is not { } last || now - last >= options.RetentionInterval)
                {
                    lastRetention = now;
                    // Settings saved from the settings page apply from the next pass; an invalid file means defaults.
                    var journal = options.ConfigPath is null ? options.Config.Config.Journal : SqlHarnessConfigLoader.Load(options.ConfigPath).Config.Journal;
                    if (journal.Enabled && journal.Retention.Enabled)
                    {
                        ...existing body: before/ReadVersion, Task.Run(JournalRetention.Run(options.DatabasePath, journal, ...)), lastVersion = ReadVersion();
                    }
                }
```

`DashboardCommand.cs`: on the `new DashboardHostOptions(...)` add `ConfigPath = SqlHarnessPaths.ConfigFile,` to its initializer (create one if absent).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: PASS, including `Housekeeping_skips_retention_when_it_is_disabled` and the idle tests.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard src/SqlHarness.Cli tests/SqlHarness.Tests/Dashboard
git commit -m "Reload dashboard retention settings before each pass"
```

---

### Task 7: UI primitives and error details on the status badge

**Files:**
- Create (CLI): `ui/src/components/ui/{dialog,tooltip,switch,input,label,dropdown-menu}.tsx`
- Create: `ui/src/lib/errors.ts`, `ui/src/lib/errors.test.ts`, `ui/src/components/StatusBadge.test.tsx`
- Modify: `ui/src/components/StatusBadge.tsx`, `ui/src/components/OperationTable.tsx:41`, `ui/src/pages/OperationPage.tsx:116`

(`ui/` = `src/SqlHarness.Dashboard/ui`.)

**Interfaces:**
- Consumes: `OperationSummary.errorMessage` (Task 2).
- Produces: `describeError(exitCode: number | null, errorKind: string | null): string`; `StatusBadge({ operation }: { operation: Pick<OperationSummary, "status" | "exitCode" | "errorKind" | "errorMessage"> })`.

- [ ] **Step 1: Add the shadcn primitives**

Run from `src/SqlHarness.Dashboard/ui`: `npx shadcn@latest add dialog tooltip switch input label dropdown-menu`
Then: `git diff src/index.css package.json` — if `src/index.css` now imports `shadcn/tailwind.css`, change it back to `@import "./styles/shadcn-tailwind.css";`; if `package.json` gained `shadcn`, run `npm uninstall shadcn`. Do not edit the generated component files. Run `npm run typecheck`.

- [ ] **Step 2: Write the failing tests**

`ui/src/lib/errors.test.ts`:

```ts
import { expect, test } from "vitest"
import { describeError } from "@/lib/errors"

test("known kinds have fixed sentences", () => {
  expect(describeError(2, "safety_rejected")).toMatch(/rejected the request before running it/)
  expect(describeError(5, "sql_execution_failed")).toMatch(/database returned an error/)
  expect(describeError(-1, "cancelled")).toMatch(/cancelled/)
})

test("unknown kinds fall back to the exit code, then to a generic sentence", () => {
  expect(describeError(3, "something_new")).toBe(describeError(3, "authentication_failed"))
  expect(describeError(99, null)).toBe("The operation failed.")
})
```

`ui/src/components/StatusBadge.test.tsx`:

```tsx
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { StatusBadge } from "@/components/StatusBadge"
import { operation } from "@/test/fixtures"

test("succeeded is a plain badge", () => {
  render(<StatusBadge operation={operation()} />)
  expect(screen.getByText("succeeded")).toBeInTheDocument()
  expect(screen.queryByRole("button")).not.toBeInTheDocument()
})

test("failed opens a dialog with the stored message", async () => {
  render(<StatusBadge operation={operation({ status: "failed", exitCode: 5, errorKind: "sql_execution_failed", errorMessage: "Invalid column name 'x'." })} />)
  await userEvent.click(screen.getByRole("button", { name: /failed/ }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent("sql_execution_failed")
  expect(dialog).toHaveTextContent("exit 5")
  expect(dialog).toHaveTextContent("Invalid column name 'x'.")
})

test("rejected without a stored message explains storeSensitive", async () => {
  render(<StatusBadge operation={operation({ status: "rejected", exitCode: 2, errorKind: "safety_rejected" })} />)
  await userEvent.click(screen.getByRole("button", { name: /rejected/ }))
  expect(await screen.findByRole("dialog")).toHaveTextContent("journal.storeSensitive")
})

test("renders error message as text", async () => {
  render(<StatusBadge operation={operation({ status: "failed", exitCode: 5, errorKind: "sql_execution_failed", errorMessage: "<img src=x onerror=alert(1)>" })} />)
  await userEvent.click(screen.getByRole("button", { name: /failed/ }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent("<img src=x onerror=alert(1)>")
  expect(dialog.querySelector("img")).toBeNull()
})

test("hover shows the error kind in a tooltip", async () => {
  render(<StatusBadge operation={operation({ status: "failed", exitCode: 5, errorKind: "sql_execution_failed" })} />)
  await userEvent.hover(screen.getByRole("button", { name: /failed/ }))
  expect(await screen.findByText(/database returned an error/)).toBeInTheDocument()
})
```

The dialog uses a link to `/settings`; render it as a plain `<a href="/settings">` (not a router `Link`) so the component works without a router in these tests.

- [ ] **Step 3: Run to verify failure**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run test -- errors StatusBadge`
Expected: FAIL (module `@/lib/errors` missing; `StatusBadge` prop shape).

- [ ] **Step 4: Implement `lib/errors.ts`**

```ts
const byKind: Record<string, string> = {
  safety_rejected: "SQLHarness rejected the request before running it (validation or safety rule).",
  authentication_failed: "The database login failed.",
  target_mismatch: "The connected server or database did not match the profile.",
  sql_execution_failed: "The database returned an error while running the SQL.",
  local_storage_failed: "SQLHarness could not write its local files or artifacts.",
  operation_failed: "The operation failed.",
  cancelled: "The operation was cancelled before it finished.",
  unhandled_exception: "SQLHarness stopped on an unexpected error.",
}

const byExitCode: Record<number, string> = {
  2: byKind.safety_rejected,
  3: byKind.authentication_failed,
  4: byKind.target_mismatch,
  5: byKind.sql_execution_failed,
  6: byKind.local_storage_failed,
}

/** A fixed, data-free sentence for an error kind; the stored message (if any) carries the details. */
export function describeError(exitCode: number | null, errorKind: string | null): string {
  return (errorKind && byKind[errorKind]) || (exitCode !== null && byExitCode[exitCode]) || byKind.operation_failed
}
```

- [ ] **Step 5: Implement `StatusBadge.tsx`**

```tsx
import { useState } from "react"
import type { OperationSummary } from "@/api/types"
import { Badge } from "@/components/ui/badge"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { describeError } from "@/lib/errors"
import { statusVariant } from "@/lib/flags"

type StatusFields = Pick<OperationSummary, "status" | "exitCode" | "errorKind" | "errorMessage">

export function StatusBadge({ operation }: { operation: StatusFields }) {
  const [open, setOpen] = useState(false)
  const badge = <Badge variant={statusVariant(operation.status)}>{operation.status}</Badge>
  if (operation.status !== "failed" && operation.status !== "rejected") return badge

  const heading = [operation.errorKind ?? "error", operation.exitCode !== null ? `exit ${operation.exitCode}` : null]
    .filter(Boolean)
    .join(" · ")
  const sentence = describeError(operation.exitCode, operation.errorKind)
  return (
    <>
      <Tooltip>
        <TooltipTrigger render={<button type="button" aria-haspopup="dialog" onClick={() => setOpen(true)} />}>
          {badge}
        </TooltipTrigger>
        <TooltipContent>
          <div>{heading}</div>
          <div>{sentence}</div>
        </TooltipContent>
      </Tooltip>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{heading}</DialogTitle>
            <DialogDescription>{sentence}</DialogDescription>
          </DialogHeader>
          {operation.errorMessage !== null ? (
            <pre className="max-h-96 overflow-auto whitespace-pre-wrap font-mono text-sm">{operation.errorMessage}</pre>
          ) : (
            <p className="text-sm text-muted-foreground">
              The full message is stored only when <code>journal.storeSensitive</code> is enabled in{" "}
              <a href="/settings">Settings</a>.
            </p>
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}
```

If the generated `tooltip.tsx` exports `TooltipProvider` and Base UI requires it, wrap the return in `<TooltipProvider>` here (one provider per badge is fine) rather than changing the generated file. If `TooltipTrigger` in the generated file does not forward `render`, use the trigger's own children API as generated (check the file) — the button must remain the focusable element with the status as its accessible name.

- [ ] **Step 6: Update call sites**

`OperationTable.tsx`: `<StatusBadge status={operation.status} />` → `<StatusBadge operation={operation} />`.
`OperationPage.tsx`: `<StatusBadge status={op.status} />` → `<StatusBadge operation={op} />`.

- [ ] **Step 7: Run UI checks**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run check`
Expected: PASS (all existing page tests still find status text).

- [ ] **Step 8: Commit**

```bash
git add src/SqlHarness.Dashboard/ui
git commit -m "Show error details for failed and rejected operations in the dashboard"
```

---

### Task 8: Theme mode with a header menu

**Files:**
- Modify: `ui/src/lib/theme.ts`, `ui/src/main.tsx`, `ui/src/components/AppLayout.tsx`
- Create: `ui/src/components/ThemeMenu.tsx`, `ui/src/lib/theme.test.ts`, `ui/src/components/ThemeMenu.test.tsx`

**Interfaces:**
- Produces: `type ThemeMode = "system" | "light" | "dark"`; `readThemeMode(): ThemeMode`; `writeThemeMode(mode: ThemeMode): void`; `applyTheme(mode: ThemeMode): void`; `useTheme(): { mode: ThemeMode; setMode: (mode: ThemeMode) => void }`; `<ThemeMenu />`.

- [ ] **Step 1: Write the failing tests**

`ui/src/lib/theme.test.ts`:

```ts
import { afterEach, expect, test, vi } from "vitest"
import { applyTheme, readThemeMode, writeThemeMode } from "@/lib/theme"

afterEach(() => {
  vi.restoreAllMocks()
  localStorage.clear()
  document.documentElement.classList.remove("dark")
})

test("mode round-trips through localStorage and defaults to system", () => {
  expect(readThemeMode()).toBe("system")
  writeThemeMode("dark")
  expect(localStorage.getItem("sqlharness.theme")).toBe("dark")
  expect(readThemeMode()).toBe("dark")
  localStorage.setItem("sqlharness.theme", "purple")
  expect(readThemeMode()).toBe("system")
})

test("storage failures fall back to system without throwing", () => {
  vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked") })
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked") })
  expect(() => writeThemeMode("light")).not.toThrow()
  expect(readThemeMode()).toBe("system")
})

test("applyTheme toggles the dark class", () => {
  applyTheme("dark")
  expect(document.documentElement).toHaveClass("dark")
  applyTheme("light")
  expect(document.documentElement).not.toHaveClass("dark")
  applyTheme("system") // the test setup's matchMedia reports light
  expect(document.documentElement).not.toHaveClass("dark")
})
```

`ui/src/components/ThemeMenu.test.tsx`:

```tsx
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { afterEach, expect, test } from "vitest"
import { ThemeMenu } from "@/components/ThemeMenu"

afterEach(() => {
  localStorage.clear()
  document.documentElement.classList.remove("dark")
})

test("choosing dark applies and remembers it", async () => {
  render(<ThemeMenu />)
  await userEvent.click(screen.getByRole("button", { name: "Theme: system" }))
  await userEvent.click(await screen.findByRole("menuitemradio", { name: "Dark" }))
  expect(document.documentElement).toHaveClass("dark")
  expect(localStorage.getItem("sqlharness.theme")).toBe("dark")
  expect(screen.getByRole("button", { name: "Theme: dark" })).toBeInTheDocument()
})
```

- [ ] **Step 2: Run to verify failure**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run test -- theme ThemeMenu`
Expected: FAIL (exports missing).

- [ ] **Step 3: Implement `lib/theme.ts`** (replace the file)

```ts
import { useCallback, useEffect, useState } from "react"

export type ThemeMode = "system" | "light" | "dark"

const storageKey = "sqlharness.theme"
const darkQuery = "(prefers-color-scheme: dark)"

/** The remembered mode; storage can be missing or blocked, which means "system". */
export function readThemeMode(): ThemeMode {
  try {
    const value = localStorage.getItem(storageKey)
    return value === "light" || value === "dark" || value === "system" ? value : "system"
  } catch {
    return "system"
  }
}

export function writeThemeMode(mode: ThemeMode): void {
  try {
    localStorage.setItem(storageKey, mode)
  } catch {
    // Not remembered; the choice still applies for this page.
  }
}

/** Toggles the `dark` class that shadcn's theme uses. */
export function applyTheme(mode: ThemeMode): void {
  const dark = mode === "dark" || (mode === "system" && window.matchMedia(darkQuery).matches)
  document.documentElement.classList.toggle("dark", dark)
}

export function useTheme(): { mode: ThemeMode; setMode: (mode: ThemeMode) => void } {
  const [mode, setModeState] = useState<ThemeMode>(readThemeMode)
  useEffect(() => {
    applyTheme(mode)
    if (mode !== "system") return
    const query = window.matchMedia(darkQuery)
    const follow = () => applyTheme("system")
    query.addEventListener("change", follow)
    return () => query.removeEventListener("change", follow)
  }, [mode])
  const setMode = useCallback((next: ThemeMode) => {
    writeThemeMode(next)
    setModeState(next)
  }, [])
  return { mode, setMode }
}
```

- [ ] **Step 4: Implement `ThemeMenu.tsx`**

```tsx
import { Monitor, Moon, Sun } from "lucide-react"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { type ThemeMode, useTheme } from "@/lib/theme"

const icons = { system: Monitor, light: Sun, dark: Moon } as const

export function ThemeMenu() {
  const { mode, setMode } = useTheme()
  const Icon = icons[mode]
  return (
    <DropdownMenu>
      <DropdownMenuTrigger render={<Button variant="ghost" size="icon" aria-label={`Theme: ${mode}`} />}>
        <Icon />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuRadioGroup value={mode} onValueChange={value => setMode(value as ThemeMode)}>
          <DropdownMenuRadioItem value="system">System</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="light">Light</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="dark">Dark</DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
```

(Match the generated `dropdown-menu.tsx` export names and `size` variants of `button.tsx`; if `size="icon"` does not exist, use `size="sm"`.)

- [ ] **Step 5: Wire it in**

`main.tsx`: remove `useSystemTheme` import and call; before `createRoot(...)` add:

```ts
import { applyTheme, readThemeMode } from "./lib/theme"
// Apply the remembered theme before the first paint; the CSP forbids an inline script in index.html.
applyTheme(readThemeMode())
```

`AppLayout.tsx`: import `ThemeMenu` and render it at the end of the header: `<div className="ml-auto"><ThemeMenu /></div>`. `AppLayout` is the root route component, so the hook lives for the whole app.

- [ ] **Step 6: Run UI checks**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run check`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Dashboard/ui
git commit -m "Let the dashboard theme be chosen as system, light or dark"
```

---

### Task 9: Formatted, highlighted SQL

**Files:**
- Modify: `ui/package.json`, `ui/package-lock.json` (via npm)
- Create: `ui/src/lib/sql.ts`, `ui/src/lib/sql.test.ts`, `ui/src/components/SqlBlock.tsx`, `ui/src/components/SqlBlock.test.tsx`
- Modify: `ui/src/index.css` (token colors), `ui/src/pages/OperationPage.tsx:175-190`, `ui/src/components/PlanTree.tsx`, `ui/src/components/VariantPanel.tsx`, `ui/src/pages/OperationPage.test.tsx:45,50`, `ui/src/components/PlanTree.test.tsx` (if it asserts SQL text)

**Interfaces:**
- Produces: `formatSql(sql: string, engine: string | null | undefined): string`; `highlightSql(sql: string): string` (escaped HTML); `<SqlBlock sql={string} engine={string | null | undefined} label?={string} />` with `<code data-testid="sql-code">`; `PlanTree({ plan, engine })`; `VariantPanel({ variant, engine })`.

- [ ] **Step 1: Add dependencies**

Run: `npm --prefix src/SqlHarness.Dashboard/ui install sql-formatter highlight.js`
Expected: both appear under `dependencies` with caret ranges; `npm --prefix src/SqlHarness.Dashboard/ui audit --omit=dev` reports no new high findings.

- [ ] **Step 2: Write the failing tests**

`ui/src/lib/sql.test.ts`:

```ts
import { expect, test } from "vitest"
import { formatSql, highlightSql } from "@/lib/sql"

test("formats T-SQL and Postgres with their dialects", () => {
  expect(formatSql("select a,b from t where x=1", "sqlserver")).toBe("select\n  a,\n  b\nfrom\n  t\nwhere\n  x = 1")
  expect(formatSql("select [a] from [t]", null)).toContain("[a]")
  expect(formatSql("select a::int from t", "postgres")).toContain("a::int")
})

test("unparsable SQL is returned unchanged", () => {
  const odd = "select ' unterminated"
  expect(formatSql(odd, "sqlserver")).toBe(odd)
})

test("escapes html in sql", () => {
  const html = highlightSql("select '<script>alert(1)</script>'")
  expect(html).not.toContain("<script>")
  expect(html).toContain("&lt;script&gt;")
  expect(html).toContain("hljs-keyword")
})
```

(If the formatter's exact layout for the first case differs because of default options, keep the options in `formatSql` and update only this expected string to the formatter's real output — the point is that it reformats.)

`ui/src/components/SqlBlock.test.tsx`:

```tsx
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { SqlBlock } from "@/components/SqlBlock"

const text = () => screen.getByTestId("sql-code").textContent

test("shows formatted SQL and toggles to the original", async () => {
  render(<SqlBlock sql="select a from t" engine="sqlserver" />)
  expect(text()).toBe("select\n  a\nfrom\n  t")
  await userEvent.click(screen.getByRole("button", { name: "Show original" }))
  expect(text()).toBe("select a from t")
  expect(screen.getByRole("button", { name: "Show formatted" })).toBeInTheDocument()
})
```

- [ ] **Step 3: Run to verify failure**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run test -- sql SqlBlock`
Expected: FAIL (modules missing).

- [ ] **Step 4: Implement `lib/sql.ts`**

```ts
import hljs from "highlight.js/lib/core"
import sqlLanguage from "highlight.js/lib/languages/sql"
import { format } from "sql-formatter"

hljs.registerLanguage("sql", sqlLanguage)

/** Formats with the engine's dialect; SQL the formatter cannot parse is shown as written. */
export function formatSql(sql: string, engine: string | null | undefined): string {
  try {
    return format(sql, { language: engine === "postgres" ? "postgresql" : "transactsql", keywordCase: "preserve" })
  } catch {
    return sql
  }
}

/** Highlighted HTML; highlight.js escapes the input, so the result is safe to inject. */
export function highlightSql(sql: string): string {
  return hljs.highlight(sql, { language: "sql", ignoreIllegals: true }).value
}
```

- [ ] **Step 5: Implement `components/SqlBlock.tsx`**

```tsx
import { useMemo, useState } from "react"
import { Button } from "@/components/ui/button"
import { formatSql, highlightSql } from "@/lib/sql"

export function SqlBlock({ sql, engine, label }: { sql: string; engine: string | null | undefined; label?: string }) {
  const [formatted, setFormatted] = useState(true)
  const text = useMemo(() => (formatted ? formatSql(sql, engine) : sql), [formatted, sql, engine])
  const html = useMemo(() => highlightSql(text), [text])
  return (
    <div className="space-y-1">
      <div className="flex items-center gap-2">
        {label && <span className="text-sm text-muted-foreground">{label}</span>}
        <Button variant="ghost" size="sm" onClick={() => setFormatted(value => !value)}>
          {formatted ? "Show original" : "Show formatted"}
        </Button>
        <Button variant="ghost" size="sm" onClick={() => void navigator.clipboard?.writeText(text)}>
          Copy
        </Button>
      </div>
      <pre className="sql-block overflow-x-auto whitespace-pre-wrap font-mono text-sm">
        {/* highlightSql escapes its input; see lib/sql.ts. */}
        <code data-testid="sql-code" dangerouslySetInnerHTML={{ __html: html }} />
      </pre>
    </div>
  )
}
```

- [ ] **Step 6: Token colors** — append to `ui/src/index.css`:

```css
/* SQL highlighting (highlight.js token classes), using the theme's chart colours so both themes stay legible. */
.sql-block .hljs-keyword,
.sql-block .hljs-built_in {
  color: var(--chart-1);
  font-weight: 600;
}
.sql-block .hljs-string {
  color: var(--chart-2);
}
.sql-block .hljs-number,
.sql-block .hljs-literal {
  color: var(--chart-3);
}
.sql-block .hljs-comment {
  color: var(--muted-foreground);
  font-style: italic;
}
.sql-block .hljs-type,
.sql-block .hljs-operator {
  color: var(--chart-4);
}
```

Check the neutral theme's `--chart-*` values in `src/index.css` / `styles/shadcn-tailwind.css` for both `:root` and `.dark`; if any chart colour is too close to `--foreground` in one theme, define `--sql-keyword` etc. under `:root` and `.dark` instead and use those.

- [ ] **Step 7: Use `SqlBlock`**

`OperationPage.tsx` SQL card body (the `<>...</>` branch):

```tsx
            <>
              <SqlBlock sql={detail.sqlText} engine={op.engine} label={detail.candidateSqlText !== null ? "Baseline" : undefined} />
              {detail.candidateSqlText !== null && <SqlBlock sql={detail.candidateSqlText} engine={op.engine} label="Candidate" />}
            </>
```

and pass `engine={op.engine}` to both `<VariantPanel variant={...} />` usages.

`VariantPanel.tsx`: add `engine: string | null` to its props and pass it: `<PlanTree plan={distilled.data} engine={engine} />`.

`PlanTree.tsx`: signature `PlanTree({ plan, engine }: { plan: DistilledPlan; engine?: string | null })`; replace the `<pre>` with `{statement.sql && <SqlBlock sql={statement.sql} engine={engine} />}`.

- [ ] **Step 8: Update the existing page tests**

`OperationPage.test.tsx` — replace `expect(screen.getByText("SELECT 2")).toBeInTheDocument()` and `expect(screen.getByText("SELECT 1 /* plan */")).toBeInTheDocument()` with:

```tsx
  const sql = () => screen.getAllByTestId("sql-code").map(code => code.textContent?.replace(/\s+/g, " ").trim())
  expect(sql()).toContain("SELECT 2")
```

and, after the plan loads:

```tsx
  expect(sql()).toContain("SELECT 1 /* plan */")
```

Apply the same pattern to any `PlanTree.test.tsx` assertion on statement SQL text (render it with `engine="sqlserver"`).

- [ ] **Step 9: Run UI checks**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run check`
Expected: PASS. If oxlint flags `dangerouslySetInnerHTML` (`react/no-danger`), add a one-line `// oxlint-disable-next-line react/no-danger -- highlightSql escapes its input` above the `<code>` instead of disabling the rule globally.

- [ ] **Step 10: Commit**

```bash
git add src/SqlHarness.Dashboard/ui
git commit -m "Format and highlight SQL in the dashboard"
```

---

### Task 10: Settings page

**Files:**
- Modify: `ui/src/api/types.ts`, `ui/src/api/client.ts`, `ui/src/api/queries.ts`, `ui/src/router.tsx`, `ui/src/components/AppLayout.tsx`, `ui/src/test/render.tsx`
- Create: `ui/src/pages/SettingsPage.tsx`, `ui/src/pages/SettingsPage.test.tsx`, `ui/src/api/client.test.ts` additions

**Interfaces:**
- Consumes: `GET/PUT /api/settings` (Task 4).
- Produces (TS):

```ts
export type JournalSettings = {
  enabled: boolean
  storeSensitive: boolean
  retention: { enabled: boolean; maxAgeDays: number; maxSizeMb: number }
}
export type DashboardSettings = { autoStart: boolean; port: number; idleShutdownHours: number }
export type Settings = { journal: JournalSettings; dashboard: DashboardSettings }
export type SettingsFileStatus = "missing" | "valid" | "invalid"
export type SettingsResponse = { status: SettingsFileStatus; path: string; settings: Settings }
export type FieldError = { field: string; message: string }
```

`putJson<T>(path: string, body: unknown, params?: QueryParams): Promise<T>`; `class FieldErrorsError extends Error { errors: FieldError[] }`; `class ConflictError extends Error`; `useSettings()`, `useSaveSettings()`.

- [ ] **Step 1: Let `stubFetch` see method and body** — in `test/render.tsx` change the handler type and call:

```tsx
export function stubFetch(routes: Record<string, unknown | ((url: URL, init?: RequestInit) => Response)>) {
  const calls: string[] = []
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://127.0.0.1")
    calls.push(url.pathname + url.search)
    const handler = routes[url.pathname + url.search] ?? routes[url.pathname]
    if (handler === undefined) return new Response("not found", { status: 404 })
    if (typeof handler === "function") return (handler as (u: URL, i?: RequestInit) => Response)(url, init)
    return new Response(JSON.stringify(handler), { status: 200, headers: { "Content-Type": "application/json" } })
  }) as typeof fetch
  return calls
}
```

- [ ] **Step 2: Write the failing tests**

Append to `ui/src/api/client.test.ts`:

```ts
test("putJson sends the write header and JSON, and maps 400 and 409", async () => {
  let seen: RequestInit | undefined
  globalThis.fetch = (async (_: RequestInfo | URL, init?: RequestInit) => {
    seen = init
    return new Response(JSON.stringify({ errors: [{ field: "journal.retention.maxAgeDays", message: "Must be between 1 and 3650." }] }), { status: 400 })
  }) as typeof fetch
  const error = await putJson("/api/settings", { a: 1 }).catch(e => e)
  expect(error).toBeInstanceOf(FieldErrorsError)
  expect((error as FieldErrorsError).errors[0].field).toBe("journal.retention.maxAgeDays")
  expect(seen?.method).toBe("PUT")
  expect(new Headers(seen?.headers).get("X-SqlHarness-Dashboard")).toBe("1")
  expect(new Headers(seen?.headers).get("Content-Type")).toBe("application/json")

  globalThis.fetch = (async () => new Response(JSON.stringify({ error: "invalid" }), { status: 409 })) as typeof fetch
  expect(await putJson("/api/settings", {}).catch(e => e)).toBeInstanceOf(ConflictError)
})
```

(Add `putJson, FieldErrorsError, ConflictError` to that file's import from `@/api/client`.)

`ui/src/pages/SettingsPage.test.tsx`:

```tsx
import { screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import type { SettingsResponse } from "@/api/types"
import { renderApp, stubFetch } from "@/test/render"

const response = (over: Partial<SettingsResponse> = {}): SettingsResponse => ({
  status: "valid",
  path: "C:\\Users\\me\\.sqlharness\\config.json",
  settings: {
    journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
    dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8 },
  },
  ...over,
})

function stubSettings(initial: SettingsResponse) {
  const puts: { body: unknown; search: string }[] = []
  stubFetch({
    "/api/settings": (url: URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        const body = JSON.parse(String(init.body))
        puts.push({ body, search: url.search })
        return new Response(JSON.stringify({ ...initial, status: "valid", settings: body }), { status: 200 })
      }
      return new Response(JSON.stringify(initial), { status: 200 })
    },
  })
  return puts
}

test("enabling storeSensitive asks for confirmation before saving", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  await userEvent.click(await screen.findByRole("switch", { name: "Store sensitive data" }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent(/SQL text, full plans/)
  expect(puts).toHaveLength(0)

  await userEvent.click(within(dialog).getByRole("button", { name: "Enable" }))
  await userEvent.click(screen.getByRole("button", { name: "Save" }))
  expect(puts).toHaveLength(1)
  expect((puts[0].body as { journal: { storeSensitive: boolean } }).journal.storeSensitive).toBe(true)
  expect(await screen.findByText(/restart running/)).toBeInTheDocument()
})

test("disabling storeSensitive needs no confirmation", async () => {
  stubSettings(response({ settings: { ...response().settings, journal: { ...response().settings.journal, storeSensitive: true } } }))
  renderApp("/settings")

  await userEvent.click(await screen.findByRole("switch", { name: "Store sensitive data" }))
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
})

test("port is read-only and an invalid file needs explicit replacement", async () => {
  const puts = stubSettings(response({ status: "invalid" }))
  renderApp("/settings")

  expect(await screen.findByText("47800")).toBeInTheDocument()
  expect(screen.queryByRole("spinbutton", { name: "Port" })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Replace with these settings" }))
  expect(puts[0].search).toBe("?overwriteInvalid=true")
})
```

- [ ] **Step 3: Run to verify failure**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run test -- client SettingsPage`
Expected: FAIL.

- [ ] **Step 4: Implement the client**

Add the TS types above to `api/types.ts` (with the header comment pointing to `DashboardSettings.cs`). In `api/client.ts`:

```ts
export class FieldErrorsError extends Error {
  constructor(readonly errors: FieldError[]) {
    super("The settings are not valid.")
    this.name = "FieldErrorsError"
  }
}

export class ConflictError extends Error {
  constructor(message: string) {
    super(message)
    this.name = "ConflictError"
  }
}

/** The dashboard's only write; the custom header is what the server's write guard requires. */
export async function putJson<T>(path: string, body: unknown, params?: QueryParams): Promise<T> {
  const url = withQuery(path, params)
  const response = await fetch(url, {
    method: "PUT",
    credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json", "X-SqlHarness-Dashboard": "1" },
    body: JSON.stringify(body),
  })
  if (response.status === 401) throw new UnauthorizedError()
  if (response.status === 400) throw new FieldErrorsError(((await response.json()) as { errors: FieldError[] }).errors)
  if (response.status === 409) throw new ConflictError(((await response.json()) as { error: string }).error)
  if (!response.ok) throw new Error(`Request failed with ${response.status}: ${url}`)
  return (await response.json()) as T
}
```

(Import `FieldError` type from `./types`.) In `api/queries.ts` add `settings: () => ["settings"] as const` to `queryKeys` and:

```ts
export function useSettings() {
  return useQuery({ queryKey: queryKeys.settings(), queryFn: () => getJson<SettingsResponse>("/api/settings") })
}

export function useSaveSettings() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ settings, overwriteInvalid }: { settings: Settings; overwriteInvalid?: boolean }) =>
      putJson<SettingsResponse>("/api/settings", settings, overwriteInvalid ? { overwriteInvalid: "true" } : undefined),
    onSuccess: data => client.setQueryData(queryKeys.settings(), data),
  })
}
```

(Import `useMutation`, `useQueryClient` from `@tanstack/react-query` and `putJson` from `./client`.)

- [ ] **Step 5: Implement `pages/SettingsPage.tsx`**

```tsx
import { useEffect, useState } from "react"
import { FieldErrorsError } from "@/api/client"
import { useSaveSettings, useSettings } from "@/api/queries"
import type { Settings } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { Switch } from "@/components/ui/switch"

function Toggle({ id, label, checked, onChange }: { id: string; label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <div className="flex items-center gap-2">
      <Switch id={id} checked={checked} onCheckedChange={onChange} aria-label={label} />
      <Label htmlFor={id}>{label}</Label>
    </div>
  )
}

function NumberField({ id, label, value, error, onChange }: { id: string; label: string; value: number; error?: string; onChange: (value: number) => void }) {
  return (
    <div className="grid gap-1">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type="number" value={value} aria-invalid={error ? true : undefined} onChange={event => onChange(Number(event.target.value))} className="w-40" />
      {error && <p className="text-sm text-destructive">{error}</p>}
    </div>
  )
}

export function SettingsPage() {
  const query = useSettings()
  const save = useSaveSettings()
  const [draft, setDraft] = useState<Settings | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    if (query.data) setDraft(query.data.settings)
  }, [query.data])

  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending || draft === null) return <Skeleton className="h-64 w-full" />

  const errors = save.error instanceof FieldErrorsError ? Object.fromEntries(save.error.errors.map(e => [e.field, e.message])) : {}
  const journal = draft.journal
  const setJournal = (next: Partial<Settings["journal"]>) => { setSaved(false); setDraft({ ...draft, journal: { ...journal, ...next } }) }
  const setRetention = (next: Partial<Settings["journal"]["retention"]>) => setJournal({ retention: { ...journal.retention, ...next } })
  const setDashboard = (next: Partial<Settings["dashboard"]>) => { setSaved(false); setDraft({ ...draft, dashboard: { ...draft.dashboard, ...next } }) }
  const submit = (overwriteInvalid = false) =>
    save.mutate({ settings: draft, overwriteInvalid }, { onSuccess: () => setSaved(true) })

  return (
    <div className="space-y-4">
      <h1>Settings</h1>
      <p className="text-sm text-muted-foreground">{query.data.path}</p>
      {query.data.status === "invalid" && (
        <Alert variant="destructive">
          <AlertTitle>config.json is invalid</AlertTitle>
          <AlertDescription>
            SQLHarness is using defaults. Saving replaces the file.
            <Button variant="outline" size="sm" onClick={() => submit(true)}>Replace with these settings</Button>
          </AlertDescription>
        </Alert>
      )}
      {saved && (
        <Alert>
          <AlertTitle>Saved</AlertTitle>
          <AlertDescription>New processes use these settings; restart running <code>mcp serve</code> sessions to apply them.</AlertDescription>
        </Alert>
      )}
      <Card>
        <CardHeader>
          <CardTitle>Activity journal</CardTitle>
          <CardDescription>What SQLHarness records about each operation.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          <Toggle id="journal-enabled" label="Journal enabled" checked={journal.enabled} onChange={value => setJournal({ enabled: value })} />
          <Toggle
            id="journal-sensitive"
            label="Store sensitive data"
            checked={journal.storeSensitive}
            onChange={value => (value ? setConfirming(true) : setJournal({ storeSensitive: false }))}
          />
          <Toggle id="retention-enabled" label="Retention enabled" checked={journal.retention.enabled} onChange={value => setRetention({ enabled: value })} />
          <NumberField id="retention-age" label="Maximum age (days)" value={journal.retention.maxAgeDays} error={errors["journal.retention.maxAgeDays"]} onChange={value => setRetention({ maxAgeDays: value })} />
          <NumberField id="retention-size" label="Maximum size (MB)" value={journal.retention.maxSizeMb} error={errors["journal.retention.maxSizeMb"]} onChange={value => setRetention({ maxSizeMb: value })} />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Dashboard</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4">
          <Toggle id="dashboard-autostart" label="Start with mcp serve" checked={draft.dashboard.autoStart} onChange={value => setDashboard({ autoStart: value })} />
          <NumberField id="dashboard-idle" label="Idle shutdown (hours)" value={draft.dashboard.idleShutdownHours} error={errors["dashboard.idleShutdownHours"]} onChange={value => setDashboard({ idleShutdownHours: value })} />
          <div className="grid gap-1">
            <span className="text-sm">Port</span>
            <span>{draft.dashboard.port}</span>
            <span className="text-sm text-muted-foreground">Change the port in config.json; the running dashboard keeps its port.</span>
          </div>
        </CardContent>
      </Card>
      {query.data.status !== "invalid" && <Button onClick={() => submit()} disabled={save.isPending}>Save</Button>}
      {save.error && !(save.error instanceof FieldErrorsError) && <ErrorState error={save.error} />}

      <Dialog open={confirming} onOpenChange={setConfirming}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Store sensitive data?</DialogTitle>
            <DialogDescription>
              From now on the journal will store SQL text, full plans (which can include parameter values), and error messages for new operations. Existing rows are not changed.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirming(false)}>Cancel</Button>
            <Button onClick={() => { setJournal({ storeSensitive: true }); setConfirming(false) }}>Enable</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
```

Notes: `ErrorState` takes `error: unknown`/`Error` — check its prop type and pass accordingly. If the generated `Switch` already exposes `role="switch"` with the `aria-label`, the test's `getByRole("switch", { name: "Store sensitive data" })` matches; keep `aria-label` even with the `Label` so the name is stable.

- [ ] **Step 6: Route and navigation**

`router.tsx`: `const settingsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/settings", component: SettingsPage })` and add it to `addChildren`. `AppLayout.tsx` `links`: add `{ to: "/settings", label: "Settings", exact: false }` (after Statistics; Profiles is added in Task 11).

- [ ] **Step 7: Run UI checks**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run check`
Expected: PASS (update `router.test.tsx` if it pins the route list).

- [ ] **Step 8: Commit**

```bash
git add src/SqlHarness.Dashboard/ui
git commit -m "Add a settings page to the dashboard"
```

---

### Task 11: Profiles page

**Files:**
- Modify: `ui/src/api/types.ts`, `ui/src/api/queries.ts`, `ui/src/router.tsx`, `ui/src/components/AppLayout.tsx`
- Create: `ui/src/pages/ProfilesPage.tsx`, `ui/src/pages/ProfilesPage.test.tsx`

**Interfaces:**
- Consumes: `GET /api/profiles` (Task 5).
- Produces (TS):

```ts
export type ProfileVariable = { name: string; rule: string }
export type ProfileView = {
  name: string
  engine: string
  server: string
  database: string
  auth: string
  sqlUser: string | null
  passwordEnvVar: string | null
  sslMode: string | null
  trustServerCertificate: boolean
  rootCertificate: string | null
  vars: ProfileVariable[]
}
export type ProfilesResponse = { status: "missing" | "valid" | "invalid"; profiles: ProfileView[]; message: string | null }
```

`useProfiles()`.

- [ ] **Step 1: Write the failing test**

```tsx
import { screen } from "@testing-library/react"
import { expect, test } from "vitest"
import { renderApp, stubFetch } from "@/test/render"

test("lists profiles with the password variable name only", async () => {
  stubFetch({
    "/api/profiles": {
      status: "valid",
      message: null,
      profiles: [{
        name: "pg", engine: "postgres", server: "pg.example", database: "app_{env}", auth: "sql", sqlUser: "reader",
        passwordEnvVar: "PG_PASSWORD", sslMode: "verify-full", trustServerCertificate: false, rootCertificate: null,
        vars: [{ name: "env", rule: "uat|test" }],
      }],
    },
  })
  renderApp("/profiles")

  expect(await screen.findByRole("cell", { name: "pg" })).toBeInTheDocument()
  expect(screen.getByText("app_{env}")).toBeInTheDocument()
  expect(screen.getByText("PG_PASSWORD")).toBeInTheDocument()
  expect(screen.getByText("env: uat|test")).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: /edit/i })).not.toBeInTheDocument()
})

test("missing and invalid files show their states", async () => {
  stubFetch({ "/api/profiles": { status: "missing", message: null, profiles: [] } })
  renderApp("/profiles")
  expect(await screen.findByText(/No targets.json/)).toBeInTheDocument()
})

test("invalid file shows the server message", async () => {
  stubFetch({ "/api/profiles": { status: "invalid", message: "targets.json could not be read. Run `sqlharness doctor` for details.", profiles: [] } })
  renderApp("/profiles")
  expect(await screen.findByText(/could not be read/)).toBeInTheDocument()
})
```

- [ ] **Step 2: Run to verify failure**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run test -- ProfilesPage`
Expected: FAIL.

- [ ] **Step 3: Implement**

Types above in `api/types.ts`. In `api/queries.ts`: `profiles: () => ["profiles"] as const` in `queryKeys` and

```ts
export function useProfiles() {
  return useQuery({ queryKey: queryKeys.profiles(), queryFn: () => getJson<ProfilesResponse>("/api/profiles") })
}
```

`pages/ProfilesPage.tsx`:

```tsx
import { useProfiles } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

export function ProfilesPage() {
  const query = useProfiles()
  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />
  const { status, profiles, message } = query.data
  return (
    <div className="space-y-4">
      <h1>Profiles</h1>
      <p className="text-sm text-muted-foreground">Read-only view of targets.json. Passwords stay in their environment variables and are never read.</p>
      {status === "missing" && <p className="text-sm text-muted-foreground">No targets.json in the SQLHarness home.</p>}
      {status === "invalid" && (
        <Alert variant="destructive">
          <AlertTitle>targets.json is invalid</AlertTitle>
          <AlertDescription>{message}</AlertDescription>
        </Alert>
      )}
      {profiles.length > 0 && (
        <div className="overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Engine</TableHead>
                <TableHead>Server</TableHead>
                <TableHead>Database</TableHead>
                <TableHead>Auth</TableHead>
                <TableHead>Password variable</TableHead>
                <TableHead>TLS</TableHead>
                <TableHead>Variables</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {profiles.map(profile => (
                <TableRow key={profile.name}>
                  <TableCell>{profile.name}</TableCell>
                  <TableCell>{profile.engine}</TableCell>
                  <TableCell>{profile.server}</TableCell>
                  <TableCell>{profile.database}</TableCell>
                  <TableCell>{profile.sqlUser ? `${profile.auth} (${profile.sqlUser})` : profile.auth}</TableCell>
                  <TableCell>{profile.passwordEnvVar ?? "—"}</TableCell>
                  <TableCell>{profile.sslMode ?? (profile.trustServerCertificate ? "trust server certificate" : "verify")}</TableCell>
                  <TableCell>
                    {profile.vars.map(variable => (
                      <div key={variable.name}>{`${variable.name}: ${variable.rule}`}</div>
                    ))}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  )
}
```

Route `/profiles` in `router.tsx`; nav link `{ to: "/profiles", label: "Profiles", exact: false }` before Settings in `AppLayout.tsx`.

- [ ] **Step 4: Run UI checks**

Run: `npm --prefix src/SqlHarness.Dashboard/ui run check`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard/ui
git commit -m "Add a read-only profiles page to the dashboard"
```

---

### Task 12: Documentation and full verification

**Files:**
- Modify: `AGENTS.md` (journal bullet in "Safety contract"; `sqlharness dashboard` bullet)
- Modify: `README.md` (dashboard section)
- Modify: `docs/superpowers/specs/2026-10-06-activity-dashboard-design.md` (top note)

- [ ] **Step 1: AGENTS.md** — in the activity journal bullet, after "SQL text is stored only with `journal.storeSensitive: true` in `~/.sqlharness/config.json`;" insert: "the full error message of a failed or rejected operation is stored under the same flag (truncated to 4096 characters);". In the `sqlharness dashboard` bullet, replace "It serves the activity journal read-only on `127.0.0.1` behind a per-start token cookie." with: "It serves the activity journal read-only on `127.0.0.1` behind a per-start token cookie, shows `targets.json` profiles read-only (password variable names only, never values), and can edit operator settings in `config.json` through its one write endpoint (`PUT /api/settings`, which also requires the `X-SqlHarness-Dashboard` header and a same-origin `Origin`; `dashboard.port` is never changed there). Saved settings apply to new processes; restart running `mcp serve` sessions."

- [ ] **Step 2: README.md** — in the dashboard section, add one paragraph: Settings page (what is editable, confirmation for `storeSensitive`, restart note), Profiles page (read-only), error details (tooltip/dialog; full message only with `storeSensitive`), theme menu (system/light/dark, remembered per browser).

- [ ] **Step 3: Old spec pointer** — under the title of `2026-10-06-activity-dashboard-design.md` add:

```markdown
> Amended by `2026-10-07-dashboard-settings-errors-theme-design.md`: one guarded write endpoint (`PUT /api/settings`), read-only profiles, stored error messages (schema v4), SQL highlighting CSS, and an operator-selectable theme.
```

- [ ] **Step 4: Full gates**

Run: `pwsh ./scripts/verify.ps1`
Expected: all stages pass (ui-install, ui-check, restore, build with `-warnaserror`, test, format check).

Run: `pwsh ./scripts/verify-linux.ps1`
Expected: all stages pass. If the format stage reports differences only in lines this plan changed, fix those lines by hand (do not run `dotnet format` over files).

- [ ] **Step 5: Manual smoke** — `dotnet run --project src/SqlHarness.Cli -- dashboard`; check: theme menu switches and survives reload; Settings saves and the file at the shown path changes; enabling storeSensitive shows the dialog; Profiles lists your profiles without passwords; a rejected operation (e.g. `sqlharness validate`-style rejected `query` with a write) shows the tooltip and dialog.

- [ ] **Step 6: Commit**

```bash
git add AGENTS.md README.md docs/superpowers/specs/2026-10-06-activity-dashboard-design.md
git commit -m "Document dashboard settings, profiles, error details and theme"
```
