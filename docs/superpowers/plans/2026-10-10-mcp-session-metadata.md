# MCP Session Metadata Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record the MCP client's `clientInfo.title`, its workspace roots, why a cancelled operation was cancelled, and allowlisted `tools/call._meta` labels (Claude Code tool-use id; Codex call id, model, effort, agent session, turn, trigger, thread source), and show them in the dashboard.

**Sequencing:** execute only after the MCP protocol upgrade branch has merged. Its real-client acceptance observed Claude Code on `2026-07-28` and Codex on `2025-06-18`. Before Task 5, prove whether request-scoped `roots/list` works on `2026-07-28`; adjust the read path or document NULL roots according to the result. The stored shape is unchanged.

**Architecture:** Journal schema v6 adds three nullable columns. Core (`SessionIdentity`, `OperationEnd`, `JournalingModule`) carries the values; the MCP layer supplies them — title from the request-scoped `ClientInfo` holder, roots from a background `McpRootsTracker` that handles handshake notifications and, if the Task 5 probe succeeds, the first eligible `2026-07-28` call, and the cancellation reason from a token-keyed registry the three MCP run sites fill. The dashboard reads and renders the new fields.

**Tech Stack:** .NET (net8/net10), xUnit, Microsoft.Data.Sqlite, ModelContextProtocol 2.2.0, React + TypeScript + vitest (dashboard UI).

**Spec:** `docs/superpowers/specs/2026-10-10-mcp-session-metadata-design.md`

## Global Constraints

- Journal failures never change tool output or exit codes; every new journal path stays inside the existing swallow-all guards.
- Stderr never carries root URIs, root names, SQL, parameter values, or secrets — only counts and exception type names.
- `client_title` ≤ 256 characters, trimmed, empty → NULL.
- Roots: at most 32 (client order), `uri` ≤ 2048 characters, `name` ≤ 256 characters; NULL = unknown, `[]` = client answered empty.
- `cancel_reason` ∈ {`shutdown`, `client`, `deadline`}, precedence `shutdown` > `client` > `deadline`; written only with `error_kind = 'cancelled'`.
- Cancelled operations keep `status = 'failed'`, `exit_code = -1`.
- Outcomes with exit `0`, `7`, `8` are never rewritten as cancellations.
- Roots fetch timeout 5 s; a newer fetch cancels the older one.
- Client roots never widen `--input-root` and never authorize a file input.
- `_meta` is read through the allowlist only (`claudecode/toolUseId`, `callId`, and `x-codex-turn-metadata.{model, reasoning_effort, session_id, turn_id, turn_trigger, thread_source}`); string values only, each ≤ 256 characters; parsing never throws.
- Both gates must pass, run one after the other: `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`.

## Review Focus

1. A client that sends `roots/list_changed` without declaring `roots.listChanged` — expected: ignored, no request sent (Task 5 host test `List_changed_without_capability_is_ignored`).
2. A safety rejection (exit 2) returned while the token happens to be cancelled — expected: recorded as cancelled with a reason; it is a cancellation race and the client also sees "cancelled" (Task 3 test `Rejected_outcome_with_cancelled_token_is_recorded_as_cancelled`).
3. A target-free tool (`gain`) running concurrently with a cancelled database call — expected: the `gain` row has no `cancel_reason`, the database row has `client` (Task 4 test `Concurrent_target_free_call_does_not_inherit_a_reason`).
4. Roots containing non-ASCII paths and `%20` escapes — expected: stored verbatim, shown decoded in the UI (Task 5 `Normalize_keeps_uris_verbatim`, Task 7 `rootLabel` test).
5. A pre-v6 journal opened by the dashboard — expected: migrated at start; old rows show `—` for roots and no cancel wording change (Task 6 `Pre_v6_rows_read_as_null`).
6. Codex `_meta` carrying sandbox mode and feature flags — expected: none of it is stored (Task 8 `Codex_meta_yields_the_allowlisted_turn_fields` compares the full record).

---

## Post-upgrade adjustments (read before Task 1)

This plan predates the MCP protocol upgrade
(`2026-10-10-mcp-protocol-upgrade.md`). That branch removed
`McpHost.PinnedProtocolVersion`, added `McpHost.CreateServerOptions` and
`McpClientIdentity`, and enabled `2026-07-28`, which has no `initialize` /
`notifications/initialized` and carries client info and capabilities per
request. It also changed the journal session supplier from a lazy identity
snapshot to a per-operation function with a fixed session key. After the
upgrade merges, start from current `main` and apply these changes below:

- **Task 2 (title):** the host builds identity from `McpClientIdentity`. Add
  a separate first-non-blank `Title` holder to `McpClientIdentity` (a first
  non-null client info object may lack title). Pass
  `clientIdentity.Title ?? running?.ClientInfo?.Title`
  (not `running?.ClientInfo?.Title`) to `SessionIdentities.Mcp` inside the
  existing per-operation `Func<SessionIdentity> identity`. Extend the
  upgrade's `McpClientIdentityTests.Journal_session_has_client_info_on_every_revision`
  to send `Title = "Claude Code"` and assert `sessions.client_title` on all three
  revisions. Extend its late-identity test so a first `2026-07-28` call without
  `clientInfo` is followed by one with `Title`, and assert the same journal
  session gains `client_title`. Add a holder test where the first client info
  has no title and a later one supplies it. Keep the single-revision
  `McpJournalTests` assertion as a focused regression.
- **Task 5 (roots):** keep the `initialized` / `roots/list_changed` triggers for
  the handshake revisions. For `2026-07-28`, first verify with a throwaway test
  whether `context.Server.RequestRootsAsync` (the request-scoped server inside a
  `tools/call` handler) works over stdio on that revision; the SDK documents
  server-to-client requests as unsupported only in stateless transport mode, and
  MRTR (`InputRequiredResult`) as the `2026-07-28` mechanism. If it works: in the
  upgrade's `tools/call` request filter, start one `McpRootsTracker.Refresh` on
  the first call whose `context.Server.ClientCapabilities?.Roots` is not null,
  using `context.Server.RequestRootsAsync`, and add a `2026-07-28` case to
  `McpRootsTests`. If it does not work: roots stay NULL on `2026-07-28` sessions;
  state that in the spec, `docs/mcp.md` and the dashboard tooltip, and do not
  implement MRTR here. `roots/list_changed` on `2026-07-28` travels over
  `subscriptions/listen`, which stays out of scope. Claude Code negotiates
  `2026-07-28` after the upgrade, so Claude roots depend on this check.
  Do not describe a declared `roots` capability as a stored snapshot until
  that request/response is proven; keep the Task 9 text conditional.
- **Task 8 (`_meta`):** on `2026-07-28` `_meta` also carries
  `io.modelcontextprotocol/*` keys; the allowlist ignores them. The upgrade's
  real-client frame logs confirm `claudecode/toolUseId` and
  `x-codex-turn-metadata` are still sent. If refreshing that check, publish a
  worktree-local build and pass its absolute path with `-Sqlharness` to
  `scripts/mcp-client-acceptance.ps1`; never assume the PATH binary is current.
- **All host tests in this plan:** run them on `2025-11-25` as written; add
  `2026-07-28` cases where the task above says so.

The upgrade's real-client frames already establish the Task 0 observations:
Claude Code supplied `title`, declared `roots` with `listChanged`, and sent
`claudecode/toolUseId`; Codex supplied `title`, declared no `roots`, and sent
`x-codex-turn-metadata`. These observations do not prove that a
`2026-07-28` `roots/list` request succeeds.

---

### Task 0: Real-client check (done — see the spike file)

**Files:**
- Create: `docs/superpowers/specs/2026-10-10-mcp-client-spike.md` (if the separate spike has not already produced it)

- [ ] **Step 1:** Read `docs/superpowers/specs/2026-10-10-mcp-client-spike.md`. If it exists and answers "does Claude Code / Codex send `clientInfo.title`" and "does each declare `capabilities.roots` (and `listChanged`)", go to Task 1.
- [ ] **Step 2:** Otherwise ask the user to run one `sqlharness_gain` call from each client against a build of this branch with a temporary stderr line in `McpHost` printing `ClientInfo.Title is null`, `ClientCapabilities.Roots is null`, `Roots.ListChanged` (booleans only), record the answers in that file, remove the temporary line, and commit the file:

```bash
git add docs/superpowers/specs/2026-10-10-mcp-client-spike.md
git commit -m "docs: record MCP client title and roots support"
```

---

### Task 1: Journal schema v6 and writes

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs` (`CurrentVersion`, new `Version6`)
- Modify: `src/SqlHarness.Core/Journal/ActivityJournal.cs` (migration chain ~line 330, `Complete` ~line 112, `UpsertSession` ~line 386)
- Modify: `src/SqlHarness.Core/Journal/JournalModels.cs` (`SessionIdentity`, `OperationEnd`)
- Test: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`

**Interfaces:**
- Produces: `SessionIdentity(..., string? Cwd, string? ClientTitle = null, string? RootsJson = null)`; `OperationEnd(..., string? ErrorMessage = null, string? CancelReason = null)`; `JournalSchema.Version6`; `JournalSchema.CurrentVersion == 6`.

- [ ] **Step 1: Write the failing tests** (append to `ActivityJournalTests`; also change the existing `Version_3_journal_migrates_to_current_and_keeps_rows` assertion from `5L` to `(long)JournalSchema.CurrentVersion`)

```csharp
    [Fact]
    public void Version_5_journal_migrates_to_current_with_null_metadata()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = JournalSchema.Version1 + JournalSchema.Version2 + JournalSchema.Version3
                + JournalSchema.Version4 + JournalSchema.Version5 + """
                INSERT INTO sessions (session_key, agent_kind, transport, source, host_pid, first_seen, last_seen)
                VALUES ('mcp:old', 'claude', 'mcp', 'mcp-clientinfo', 1, 't', 't');
                INSERT INTO operations (session_id, operation, host_pid, started_at, updated_at, status, error_kind)
                VALUES (1, 'query', 1, 't', 't', 'failed', 'cancelled');
                PRAGMA user_version = 5;
                """;
            command.ExecuteNonQuery();
        }

        Open(temp, TextWriter.Null);

        Assert.Equal(6L, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        var session = JournalDb.Rows(temp.DatabasePath, "SELECT client_title, roots_json FROM sessions")[0];
        Assert.Null(session["client_title"]);
        Assert.Null(session["roots_json"]);
        Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT cancel_reason FROM operations")[0]["cancel_reason"]);
    }

    [Fact]
    public void Session_title_keeps_first_value_and_roots_keep_latest_non_null()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);
        var baseSession = JournalTestData.Session("mcp:meta");

        journal.Begin(baseSession with { ClientTitle = null, RootsJson = null }, JournalTestData.Start());
        AssertSession(null, null);

        journal.Begin(baseSession with { ClientTitle = "Claude Code", RootsJson = "[]" }, JournalTestData.Start());
        AssertSession("Claude Code", "[]");

        journal.Begin(baseSession with { ClientTitle = "Other", RootsJson = """[{"uri":"file:///a"}]""" }, JournalTestData.Start());
        AssertSession("Claude Code", """[{"uri":"file:///a"}]""");

        journal.Begin(baseSession with { ClientTitle = null, RootsJson = null }, JournalTestData.Start());
        AssertSession("Claude Code", """[{"uri":"file:///a"}]""");

        void AssertSession(string? title, string? roots)
        {
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT client_title, roots_json FROM sessions").Single();
            Assert.Equal(title, row["client_title"]);
            Assert.Equal(roots, row["roots_json"]);
        }
    }

    [Fact]
    public void Cancel_reason_is_stored_with_the_end_row()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());

        Assert.True(journal.Complete(handle, new OperationEnd("failed", -1, "cancelled", 5, null, null, null, null, null, CancelReason: "client")));

        var row = JournalDb.Rows(temp.DatabasePath, "SELECT error_kind, cancel_reason FROM operations").Single();
        Assert.Equal("cancelled", row["error_kind"]);
        Assert.Equal("client", row["cancel_reason"]);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ActivityJournalTests"`
Expected: compile errors (`JournalSchema.Version4`/`Version5` are fine; `ClientTitle`, `RootsJson`, `CancelReason` do not exist).

- [ ] **Step 3: Implement**

`JournalModels.cs` — extend the records (new members last, with defaults, so every existing positional call keeps compiling):

```csharp
public sealed record SessionIdentity(
    string SessionKey,
    string AgentKind,
    string Source,
    JournalTransport Transport,
    string? ClientName,
    string? ClientVersion,
    string? McpMode,
    int? AgentPid,
    DateTimeOffset? AgentStartedAt,
    int HostPid,
    DateTimeOffset? HostStartedAt,
    string? Cwd,
    string? ClientTitle = null,
    string? RootsJson = null);
```

```csharp
public sealed record OperationEnd(
    string Status,
    int ExitCode,
    string? ErrorKind,
    long DurationMilliseconds,
    string? Engine,
    string? Server,
    string? Database,
    int? ResultSets,
    long? RowsReturned,
    long? RawTokens = null,
    string? ArtifactDirectory = null,
    string? SummaryJson = null,
    string? ErrorMessage = null,
    string? CancelReason = null);
```

`JournalSchema.cs`:

```csharp
    internal const int CurrentVersion = 6;
```

```csharp
    internal const string Version6 = """
        ALTER TABLE sessions ADD COLUMN client_title TEXT;
        ALTER TABLE sessions ADD COLUMN roots_json TEXT;
        ALTER TABLE operations ADD COLUMN cancel_reason TEXT;
        """;
```

`ActivityJournal.cs` migration chain, after the `locked < 5` line:

```csharp
            if (locked < 6)
                Execute(connection, JournalSchema.Version6);
```

`ActivityJournal.Complete` — add `cancel_reason = $cancel` to the `UPDATE operations SET` list and the parameter:

```csharp
            update.Parameters.AddWithValue("$cancel", (object?)end.CancelReason ?? DBNull.Value);
```

`UpsertSession` — replace the command text and add two parameters:

```csharp
        upsert.CommandText = """
            INSERT INTO sessions (session_key, agent_kind, transport, source, client_name, client_version, client_title, mcp_mode,
                agent_pid, agent_started_at, host_pid, host_started_at, cwd, roots_json, first_seen, last_seen)
            VALUES ($key, $kind, $transport, $source, $clientName, $clientVersion, $clientTitle, $mcpMode,
                $agentPid, $agentStarted, $hostPid, $hostStarted, $cwd, $roots, $now, $now)
            ON CONFLICT(session_key) DO UPDATE SET
                last_seen = excluded.last_seen,
                client_name = COALESCE(sessions.client_name, excluded.client_name),
                client_version = COALESCE(sessions.client_version, excluded.client_version),
                client_title = COALESCE(sessions.client_title, excluded.client_title),
                roots_json = COALESCE(excluded.roots_json, sessions.roots_json)
            RETURNING id;
            """;
```

```csharp
        upsert.Parameters.AddWithValue("$clientTitle", (object?)session.ClientTitle ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$roots", (object?)session.RootsJson ?? DBNull.Value);
```

- [ ] **Step 4: Run the journal tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`
Expected: PASS (including the edited `Version_3_...` test).

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/Journal tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs
git commit -m "feat(journal): schema v6 with client title, roots and cancel reason"
```

---

### Task 2: Client title on MCP sessions

**Files:**
- Modify: `src/SqlHarness.Core/Journal/SessionIdentities.cs` (`Mcp`)
- Modify: `src/SqlHarness.Mcp/McpHost.cs:115`
- Test: `tests/SqlHarness.Tests/Journal/SessionIdentitiesTests.cs`, `tests/SqlHarness.Mcp.Tests/McpJournalTests.cs`

**Interfaces:**
- Consumes: `SessionIdentity.ClientTitle` (Task 1).
- Produces: `SessionIdentities.Mcp(IProcessInfo processes, string sessionKey, string? clientName, string? clientVersion, string mcpMode, string? clientTitle = null)`; `SessionIdentities.MaximumTitleLength = 256`.

- [ ] **Step 1: Write the failing tests**

In `SessionIdentitiesTests` (reuse the `FakeProcesses` / `P` helpers already in the file):

```csharp
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Claude Code  ", "Claude Code")]
    public void Mcp_normalizes_client_title(string? supplied, string? expected)
    {
        var processes = new FakeProcesses(30, P(30, null, "sqlharness.exe"));

        var identity = SessionIdentities.Mcp(processes, "mcp:abc", "claude-code", "2.1.0", "fixed", supplied);

        Assert.Equal(expected, identity.ClientTitle);
    }

    [Fact]
    public void Mcp_caps_client_title_length()
    {
        var processes = new FakeProcesses(30, P(30, null, "sqlharness.exe"));

        var identity = SessionIdentities.Mcp(processes, "mcp:abc", "x", "1", "fixed", new string('t', 300));

        Assert.Equal(SessionIdentities.MaximumTitleLength, identity.ClientTitle!.Length);
    }
```

In `McpJournalTests.Tool_call_records_session_from_client_info`: set `ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9", Title = "Claude Code" }`, add `s.client_title` as the last selected column, and assert:

```csharp
        Assert.Equal("Claude Code", reader.GetString(9));
```

Also extend `McpClientIdentityTests.Journal_session_has_client_info_on_every_revision`
with the title assertion on each revision. In its `2026-07-28` late-identity
case, strip `clientInfo` through the first tool call, supply a title on the
later call, and assert `client_title` becomes non-NULL in that same session.
Assert the holder separately accepts a title from later client info even when
the first non-null client info lacked one.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SessionIdentitiesTests"` and `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpJournalTests"`
Expected: compile error (no 6th parameter) / assertion failure (`client_title` NULL).

- [ ] **Step 3: Implement**

`SessionIdentities.cs` — add the constant next to `MaximumDepth`:

```csharp
    internal const int MaximumTitleLength = 256;
```

change the `Mcp` signature to:

```csharp
    public static SessionIdentity Mcp(IProcessInfo processes, string sessionKey, string? clientName, string? clientVersion, string mcpMode, string? clientTitle = null)
```

and change the `return new SessionIdentity(...)` in `Mcp` to end with:

```csharp
            processes.CurrentPid,
            walk.Self?.StartedAt,
            CurrentDirectory(),
            ClientTitle: Title(clientTitle));
```

and add:

```csharp
    private static string? Title(string? clientTitle)
    {
        var trimmed = clientTitle?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= MaximumTitleLength ? trimmed : trimmed[..MaximumTitleLength];
    }
```

`McpHost.cs` — keep the fixed session key and existing per-operation identity
function. Add the title as its final `SessionIdentities.Mcp` argument:

```csharp
        Func<SessionIdentity> identity = () => SessionIdentities.Mcp(
            ProcessInfo.Current,
            sessionKey,
            clientIdentity.Name ?? running?.ClientInfo?.Name,
            clientIdentity.Version ?? running?.ClientInfo?.Version,
            mcpMode,
            clientIdentity.Title ?? running?.ClientInfo?.Title);
```

In `McpClientIdentity`, add a separate `private string? _title;`. When
`clientInfo.Title` is not null or whitespace, record it with
`Interlocked.CompareExchange(ref _title, clientInfo.Title, null)` even when
`_client` was already set, and expose it
through `public string? Title => Volatile.Read(ref _title);`. This preserves
the existing first-client-info rule for name and version while allowing a
later title to fill NULL. Keep passing `identity` to `JournalingModule`; do
not reintroduce a lazy `SessionIdentity` snapshot.

- [ ] **Step 4: Run the tests**

Same commands as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/Journal/SessionIdentities.cs src/SqlHarness.Mcp/McpClientIdentity.cs src/SqlHarness.Mcp/McpHost.cs tests/SqlHarness.Tests/Journal/SessionIdentitiesTests.cs tests/SqlHarness.Mcp.Tests/McpJournalTests.cs tests/SqlHarness.Mcp.Tests/McpClientIdentityTests.cs
git commit -m "feat(mcp): record clientInfo.title on MCP sessions"
```

---

### Task 3: JournalingModule — cancellation reason, translated cancellations, roots provider

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalingModule.cs`
- Modify: `src/SqlHarness.Core/Journal/OperationJournalDescriber.cs:63` (`Cancelled`)
- Test: `tests/SqlHarness.Tests/Journal/JournalingModuleTests.cs`

**Interfaces:**
- Consumes: `OperationEnd.CancelReason`, `SessionIdentity.RootsJson` (Task 1).
- Produces:
  - `JournalingModule(ISqlHarnessModule inner, Func<IActivityJournal> journal, Func<SessionIdentity> session, Func<CancellationToken, string?>? cancelReason = null, Func<string?>? rootsJson = null)`
  - `OperationJournalDescriber.Cancelled(long durationMilliseconds, string? reason = null)`

- [ ] **Step 1: Write the failing tests**

In `JournalingModuleTests`, replace `Create` with an overload that accepts the providers:

```csharp
    private static (JournalingModule Module, JournalTempDirectory Temp) Create(
        FakeModule inner,
        bool storeSensitive = false,
        Func<CancellationToken, string?>? cancelReason = null,
        Func<string?>? rootsJson = null)
    {
        var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, TimeProvider.System);
        return (new JournalingModule(inner, () => journal, () => JournalTestData.Session(), cancelReason, rootsJson), temp);
    }
```

Add:

```csharp
    [Theory]
    [InlineData("client")]
    [InlineData("shutdown")]
    [InlineData("deadline")]
    [InlineData(null)]
    public async Task Thrown_cancellation_records_the_provider_reason(string? reason)
    {
        CancellationToken seen = default;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (module, temp) = Create(
            new FakeModule((_, ct) => Task.FromCanceled<SqlHarnessOutcome>(ct)),
            cancelReason: token => { seen = token; return reason; });
        using (temp)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ExecuteAsync(Query(), cts.Token));

            Assert.Equal(cts.Token, seen);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT error_kind, cancel_reason FROM operations").Single();
            Assert.Equal("cancelled", row["error_kind"]);
            Assert.Equal(reason, row["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Throwing_reason_provider_records_null_and_rethrows_the_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (module, temp) = Create(
            new FakeModule((_, ct) => Task.FromCanceled<SqlHarnessOutcome>(ct)),
            cancelReason: _ => throw new InvalidOperationException("provider"));
        using (temp)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ExecuteAsync(Query(), cts.Token));
            Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT cancel_reason FROM operations").Single()["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Translated_sql_failure_with_cancelled_token_is_recorded_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var failure = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "The operation was canceled.");
        var (module, temp) = Create(
            new FakeModule(async (_, _) => { await cts.CancelAsync(); return failure; }),
            cancelReason: _ => "client");
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query(), cts.Token);

            Assert.Same(failure, outcome);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, exit_code, error_kind, cancel_reason FROM operations").Single();
            Assert.Equal("failed", row["status"]);
            Assert.Equal(-1L, row["exit_code"]);
            Assert.Equal("cancelled", row["error_kind"]);
            Assert.Equal("client", row["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Rejected_outcome_with_cancelled_token_is_recorded_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var (module, temp) = Create(
            new FakeModule(async (_, _) =>
            {
                await cts.CancelAsync();
                return new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "SQL safety rejection: nope");
            }),
            cancelReason: _ => "shutdown");
        using (temp)
        {
            await module.ExecuteAsync(Query(), cts.Token);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT error_kind, cancel_reason FROM operations").Single();
            Assert.Equal("cancelled", row["error_kind"]);
            Assert.Equal("shutdown", row["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Sql_failure_without_cancellation_stays_an_sql_failure()
    {
        var (module, temp) = Create(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "boom"))),
            cancelReason: _ => "client");
        using (temp)
        {
            await module.ExecuteAsync(Query(), CancellationToken.None);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT exit_code, error_kind, cancel_reason FROM operations").Single();
            Assert.Equal(5L, row["exit_code"]);
            Assert.NotEqual("cancelled", row["error_kind"]);
            Assert.Null(row["cancel_reason"]);
        }
    }

    [Theory]
    [InlineData(SqlHarnessExitCode.Success)]
    [InlineData(SqlHarnessExitCode.WatchMaxDuration)]
    [InlineData(SqlHarnessExitCode.SnapshotDifferences)]
    public async Task Controlled_success_with_cancelled_token_is_not_rewritten(SqlHarnessExitCode exitCode)
    {
        using var cts = new CancellationTokenSource();
        var (module, temp) = Create(
            new FakeModule(async (_, _) => { await cts.CancelAsync(); return new SqlHarnessOutcome(exitCode, null, null); }),
            cancelReason: _ => "client");
        using (temp)
        {
            await module.ExecuteAsync(Query(), cts.Token);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, cancel_reason FROM operations").Single();
            Assert.Equal("succeeded", row["status"]);
            Assert.Null(row["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Roots_provider_is_read_at_each_begin()
    {
        var roots = (string?)null;
        var (module, temp) = Create(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            rootsJson: () => roots);
        using (temp)
        {
            await module.ExecuteAsync(Query());
            Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT roots_json FROM sessions").Single()["roots_json"]);

            roots = """[{"uri":"file:///w"}]""";
            await module.ExecuteAsync(Query());
            Assert.Equal(roots, JournalDb.Rows(temp.DatabasePath, "SELECT roots_json FROM sessions").Single()["roots_json"]);
        }
    }
```

(`SqlHarnessExitCode` must be usable in `[InlineData]`; it is a public enum. If `JournalTestData.Session()` is shared, both `ExecuteAsync` calls hit the same `cli:test` session row — that is what the roots test needs.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalingModuleTests"`
Expected: compile error (constructor has no 4th/5th parameter).

- [ ] **Step 3: Implement**

`OperationJournalDescriber.cs`:

```csharp
    internal static OperationEnd Cancelled(long durationMilliseconds, string? reason = null) =>
        new("failed", -1, "cancelled", Math.Max(durationMilliseconds, 0), null, null, null, null, null, CancelReason: reason);
```

`JournalingModule.cs` — fields and constructor:

```csharp
    private readonly Func<CancellationToken, string?>? _cancelReason;
    private readonly Func<string?>? _rootsJson;

    public JournalingModule(
        ISqlHarnessModule inner,
        Func<IActivityJournal> journal,
        Func<SessionIdentity> session,
        Func<CancellationToken, string?>? cancelReason = null,
        Func<string?>? rootsJson = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(session);
        _journal = new Lazy<IActivityJournal?>(() => Try(journal), LazyThreadSafetyMode.ExecutionAndPublication);
        // Per operation, as the upgrade made it: a later request may supply client info.
        _session = () => Try(session);
        _cancelReason = cancelReason;
        _rootsJson = rootsJson;
    }
```

Pass the token into `RunAsync`:

```csharp
    public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
        RunAsync(operation, effective => _inner.ExecuteAsync(effective, ct), ct);

    public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation operation,
        TextWriter writer,
        CancellationToken ct = default) =>
        RunAsync(operation, effective => _inner.ExecuteWatchNdjsonAsync((SqlHarnessWatchOperation)effective, writer, ct), ct);
```

In `RunAsync(SqlHarnessOperation operation, Func<SqlHarnessOperation, Task<SqlHarnessOutcome>> run, CancellationToken ct)`:

```csharp
        catch (OperationCanceledException)
        {
            Complete(journal, handle, () => OperationJournalDescriber.Cancelled(stopwatch.ElapsedMilliseconds, Reason(ct)));
            throw;
        }
```

and immediately after the `try/catch` (before the existing `var completed = ...` line):

```csharp
        // Core maps a cancellation during SQL to an ordinary failed outcome after disposing
        // its session; MCP reports such a call as cancelled, so the journal does too.
        if (ct.IsCancellationRequested && IsCancellableFailure(outcome.ExitCode))
        {
            Complete(journal, handle, () => OperationJournalDescriber.Cancelled(stopwatch.ElapsedMilliseconds, Reason(ct)));
            return outcome;
        }
```

New helpers:

```csharp
    private static bool IsCancellableFailure(SqlHarnessExitCode exitCode) =>
        exitCode is not (SqlHarnessExitCode.Success or SqlHarnessExitCode.WatchMaxDuration or SqlHarnessExitCode.SnapshotDifferences);

    private string? Reason(CancellationToken ct)
    {
        if (_cancelReason is null)
            return null;
        try
        {
            return _cancelReason(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? Roots()
    {
        if (_rootsJson is null)
            return null;
        try
        {
            return _rootsJson();
        }
        catch (Exception)
        {
            return null;
        }
    }
```

In `Begin`, attach the current roots:

```csharp
            var current = _rootsJson is null ? session : session with { RootsJson = Roots() };
            return journal.Begin(current, OperationJournalDescriber.DescribeStart(operation));
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`
Expected: PASS, including the pre-existing `Cancelled_operation_is_completed_as_cancelled_and_rethrown`.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Core/Journal/JournalingModule.cs src/SqlHarness.Core/Journal/OperationJournalDescriber.cs tests/SqlHarness.Tests/Journal/JournalingModuleTests.cs
git commit -m "feat(journal): record cancellation reason and translated cancellations"
```

---

### Task 4: MCP call registry (cancellation reason)

**Files:**
- Create: `src/SqlHarness.Mcp/McpCallRegistry.cs`
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`RunAsync` ~line 348, `RunDbAsync` ~line 405)
- Modify: `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs` (`RunAsync` ~line 180)
- Modify: `src/SqlHarness.Mcp/McpHost.cs:117` (pass the provider)
- Create: `tests/SqlHarness.Mcp.Tests/McpCallRegistryTests.cs`

**Interfaces:**
- Consumes: `JournalingModule(..., Func<CancellationToken, string?>? cancelReason, ...)` (Task 3).
- Produces:
  - `internal static class McpCallRegistry { IDisposable Track(CancellationTokenSource linked, CancellationToken request, CancellationToken shutdown); string? Classify(CancellationToken moduleToken); }`
  - constants `McpCallRegistry.Shutdown = "shutdown"`, `Client = "client"`, `Deadline = "deadline"`.

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Mcp.Tests/McpCallRegistryTests.cs`:

```csharp
using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

public sealed class McpCallRegistryTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-cancel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void Classify_prefers_shutdown_then_client_then_deadline()
    {
        using var request = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, shutdown.Token);
        using (McpCallRegistry.Track(linked, request.Token, shutdown.Token))
        {
            Assert.Null(McpCallRegistry.Classify(linked.Token));
            linked.Cancel();
            Assert.Equal("deadline", McpCallRegistry.Classify(linked.Token));
            request.Cancel();
            Assert.Equal("client", McpCallRegistry.Classify(linked.Token));
            shutdown.Cancel();
            Assert.Equal("shutdown", McpCallRegistry.Classify(linked.Token));
        }

        Assert.Null(McpCallRegistry.Classify(linked.Token));
    }

    [Fact]
    public void Unknown_token_has_no_reason()
    {
        Assert.Null(McpCallRegistry.Classify(new CancellationToken(canceled: true)));
    }

    private static McpScope Scope(int maxOperationSeconds = 300) => McpScope.Create(
        new McpServerOptions { Profile = "mcp-t5", MaxOperationSeconds = maxOperationSeconds },
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
        });

    private sealed class BlockingModule : ISqlHarnessModule
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            if (operation is SqlHarnessGainOperation)
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("A cancelled wait never completes.");
        }
    }

    private (McpToolHandlers Handlers, BlockingModule Inner, string Database) Create(
        CancellationToken hostShutdown, int maxOperationSeconds = 300)
    {
        var database = Path.Combine(_root, "data", "activity.db");
        var journal = ActivityJournal.Open(database, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var inner = new BlockingModule();
        var module = new JournalingModule(
            inner,
            () => journal,
            () => new SessionIdentity("mcp:test", "claude", "mcp-clientinfo", JournalTransport.Mcp, "c", "1", "fixed", null, null, 1, null, null),
            McpCallRegistry.Classify);
        return (new McpToolHandlers(Scope(maxOperationSeconds), module, new McpExecutionGate(), hostShutdown: hostShutdown), inner, database);
    }

    private static string? ReasonOf(string database, string operation)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cancel_reason FROM operations WHERE operation = $op ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$op", operation);
        var value = command.ExecuteScalar();
        return value is DBNull or null ? null : (string)value;
    }

    [Fact]
    public async Task Client_cancellation_is_recorded_as_client()
    {
        var (handlers, inner, database) = Create(CancellationToken.None);
        using var request = new CancellationTokenSource(Budget);

        var call = handlers.QueryAsync(null!, "SELECT 1", ct: request.Token);
        await inner.Entered.Task.WaitAsync(Budget);
        await request.CancelAsync();
        await call.WaitAsync(Budget);

        Assert.Equal("client", ReasonOf(database, "query"));
    }

    [Fact]
    public async Task Host_shutdown_is_recorded_as_shutdown()
    {
        using var shutdown = new CancellationTokenSource();
        var (handlers, inner, database) = Create(shutdown.Token);
        using var request = new CancellationTokenSource(Budget);

        var call = handlers.QueryAsync(null!, "SELECT 1", ct: request.Token);
        await inner.Entered.Task.WaitAsync(Budget);
        await shutdown.CancelAsync();
        await request.CancelAsync();
        await call.WaitAsync(Budget);

        Assert.Equal("shutdown", ReasonOf(database, "query"));
    }

    [Fact]
    public async Task Exhausted_call_budget_is_recorded_as_deadline()
    {
        var (handlers, _, database) = Create(CancellationToken.None, maxOperationSeconds: 1);
        using var request = new CancellationTokenSource(Budget);

        await handlers.QueryAsync(null!, "SELECT 1", ct: request.Token).WaitAsync(Budget);

        Assert.Equal("deadline", ReasonOf(database, "query"));
    }

    [Fact]
    public async Task Concurrent_target_free_call_does_not_inherit_a_reason()
    {
        var (handlers, inner, database) = Create(CancellationToken.None);
        using var request = new CancellationTokenSource(Budget);

        var call = handlers.QueryAsync(null!, "SELECT 1", ct: request.Token);
        await inner.Entered.Task.WaitAsync(Budget);
        await request.CancelAsync();
        await handlers.GainAsync(null!, ct: CancellationToken.None).WaitAsync(Budget);
        await call.WaitAsync(Budget);

        Assert.Equal("client", ReasonOf(database, "query"));
        Assert.Null(ReasonOf(database, "gain"));
    }
}
```

Before running, open `McpToolCatalog.cs` and confirm the exact `QueryAsync` / `GainAsync` parameter lists on `McpToolHandlers` (the cancellation tests call `handlers.QueryAsync(null!, "SELECT 1", ct: ...)`; use the same shape for `GainAsync`, passing `null!` for the request context if its first parameter is one).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpCallRegistryTests"`
Expected: compile error (`McpCallRegistry` does not exist).

- [ ] **Step 3: Implement**

`src/SqlHarness.Mcp/McpCallRegistry.cs`:

```csharp
using System.Collections.Concurrent;

namespace SqlHarness.Mcp;

/// <summary>
/// Maps the token an MCP run site hands to the module back to why it was cancelled.
/// Keyed by token, not by "the current call": target-free tools take no gate and
/// can run beside a database call.
/// </summary>
internal static class McpCallRegistry
{
    internal const string Shutdown = "shutdown";
    internal const string Client = "client";
    internal const string Deadline = "deadline";

    private static readonly ConcurrentDictionary<CancellationToken, (CancellationToken Request, CancellationToken Shutdown)> Calls = new();

    internal static IDisposable Track(CancellationTokenSource linked, CancellationToken request, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(linked);
        var token = linked.Token;
        Calls[token] = (request, shutdown);
        return new Registration(token);
    }

    internal static string? Classify(CancellationToken moduleToken)
    {
        if (!moduleToken.IsCancellationRequested || !Calls.TryGetValue(moduleToken, out var call))
            return null;
        if (call.Shutdown.IsCancellationRequested)
            return Shutdown;
        return call.Request.IsCancellationRequested ? Client : Deadline;
    }

    private sealed class Registration(CancellationToken token) : IDisposable
    {
        public void Dispose() => Calls.TryRemove(token, out _);
    }
}
```

In each of the three run sites, right after `using var linked = CancellationTokenSource.CreateLinkedTokenSource(...)`:

```csharp
            using var reason = McpCallRegistry.Track(linked, ct, _hostShutdown);
```

(`McpRequestToolHandlers.RunAsync` uses the field name `hostShutdown`, not `_hostShutdown`.)

`McpHost.cs:117`:

```csharp
        process.DecorateModules(module => new JournalingModule(
            module,
            () => journal.Value,
            identity,
            McpCallRegistry.Classify));
```

- [ ] **Step 4: Run the MCP tests**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~Cancellation"`
Expected: PASS (new tests and existing `McpCancellationTests`).

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests/McpCallRegistryTests.cs
git commit -m "feat(mcp): classify cancellations as client, shutdown or deadline"
```

---

### Task 5: Workspace roots tracker

**Files:**
- Create: `src/SqlHarness.Mcp/McpRootsTracker.cs`
- Modify: `src/SqlHarness.Mcp/McpHost.cs` (tracker, notification handlers, `rootsJson` provider)
- Create: `tests/SqlHarness.Mcp.Tests/McpRootsTests.cs`

**Interfaces:**
- Consumes: `JournalingModule(..., Func<string?>? rootsJson)` (Task 3).
- Produces:
  - `internal sealed class McpRootsTracker(TextWriter log, CancellationToken lifetime, TimeSpan? timeout = null)` with `string? Current { get; }`, `Task Refresh(Func<CancellationToken, ValueTask<ListRootsResult>> request)`, `static (string Json, int Kept, int Total) Normalize(IList<Root>? roots)`, constants `MaximumRoots = 32`, `MaximumUriLength = 2048`, `MaximumNameLength = 256`, `DefaultTimeout = 5 s`.

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Mcp.Tests/McpRootsTests.cs`:

```csharp
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpRootsTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-roots-" + Guid.NewGuid().ToString("N"));

    public McpRootsTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_home, true);
    }

    // ---- Normalize ----

    [Fact]
    public void Normalize_omits_missing_names_and_keeps_order()
    {
        var (json, kept, total) = McpRootsTracker.Normalize(
            [new Root { Uri = "file:///b", Name = "b" }, new Root { Uri = "file:///a" }]);

        Assert.Equal("""[{"uri":"file:///b","name":"b"},{"uri":"file:///a"}]""", json);
        Assert.Equal((2, 2), (kept, total));
    }

    [Fact]
    public void Normalize_of_empty_or_missing_list_is_an_empty_array()
    {
        Assert.Equal("[]", McpRootsTracker.Normalize([]).Json);
        Assert.Equal("[]", McpRootsTracker.Normalize(null).Json);
    }

    [Fact]
    public void Normalize_applies_count_and_length_limits()
    {
        var roots = Enumerable.Range(0, 40)
            .Select(i => new Root { Uri = "file:///" + new string('u', 3000) + i, Name = new string('n', 300) })
            .ToList();

        var (json, kept, total) = McpRootsTracker.Normalize(roots);

        Assert.Equal((32, 40), (kept, total));
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(32, parsed.GetArrayLength());
        Assert.Equal(McpRootsTracker.MaximumUriLength, parsed[0].GetProperty("uri").GetString()!.Length);
        Assert.Equal(McpRootsTracker.MaximumNameLength, parsed[0].GetProperty("name").GetString()!.Length);
    }

    [Fact]
    public void Normalize_keeps_uris_verbatim()
    {
        const string uri = "file:///D:/Projekty/Za%C5%BC%C3%B3%C5%82%C4%87%20g%C4%99%C5%9Bl%C4%85";
        var json = McpRootsTracker.Normalize([new Root { Uri = uri }]).Json;
        Assert.Equal(uri, JsonDocument.Parse(json).RootElement[0].GetProperty("uri").GetString());
    }

    // ---- Tracker ----

    private static Func<CancellationToken, ValueTask<ListRootsResult>> Answer(params string[] uris) =>
        _ => ValueTask.FromResult(new ListRootsResult { Roots = uris.Select(u => new Root { Uri = u }).ToList() });

    [Fact]
    public async Task Successful_fetch_publishes_the_snapshot()
    {
        var tracker = new McpRootsTracker(TextWriter.Null, CancellationToken.None);
        Assert.Null(tracker.Current);

        await tracker.Refresh(Answer("file:///w")).WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///w"}]""", tracker.Current);
    }

    [Fact]
    public async Task Failed_fetch_keeps_previous_snapshot_and_logs_no_uri()
    {
        var log = new StringWriter();
        var tracker = new McpRootsTracker(log, CancellationToken.None);
        await tracker.Refresh(Answer("file:///SQLH_ROOT_MARKER")).WaitAsync(Budget);

        await tracker.Refresh(_ => throw new InvalidOperationException("file:///SQLH_ROOT_MARKER")).WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///SQLH_ROOT_MARKER"}]""", tracker.Current);
        Assert.Contains("InvalidOperationException", log.ToString());
        Assert.DoesNotContain("SQLH_ROOT_MARKER", log.ToString());
    }

    [Fact]
    public async Task Timed_out_fetch_keeps_previous_snapshot()
    {
        var log = new StringWriter();
        var tracker = new McpRootsTracker(log, CancellationToken.None, TimeSpan.FromMilliseconds(100));
        await tracker.Refresh(Answer("file:///old")).WaitAsync(Budget);

        await tracker.Refresh(async ct => { await Task.Delay(Timeout.Infinite, ct); return new ListRootsResult(); }).WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///old"}]""", tracker.Current);
        Assert.Contains("timed out", log.ToString());
    }

    [Fact]
    public async Task Newer_fetch_wins_over_a_slow_older_one()
    {
        var tracker = new McpRootsTracker(TextWriter.Null, CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = tracker.Refresh(async _ =>
        {
            await release.Task; // ignores cancellation on purpose: a late answer must still lose
            return new ListRootsResult { Roots = [new Root { Uri = "file:///old" }] };
        });
        await tracker.Refresh(Answer("file:///new")).WaitAsync(Budget);
        release.SetResult();
        await slow.WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///new"}]""", tracker.Current);
    }

    [Fact]
    public async Task Truncation_is_reported_as_counts_only()
    {
        var log = new StringWriter();
        var tracker = new McpRootsTracker(log, CancellationToken.None);

        await tracker.Refresh(Answer(Enumerable.Range(0, 40).Select(i => "file:///SQLH_ROOT_MARKER" + i).ToArray())).WaitAsync(Budget);

        Assert.Contains("kept 32 of 40", log.ToString());
        Assert.DoesNotContain("SQLH_ROOT_MARKER", log.ToString());
    }

    // ---- Host ----

    private static Func<IReadOnlyDictionary<string, TargetProfile>> Profiles => () =>
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
        };

    private string? RootsJson()
    {
        var database = Path.Combine(_home, "data", "activity.db");
        if (!File.Exists(database))
            return null;
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT roots_json FROM sessions";
        var value = command.ExecuteScalar();
        return value is DBNull or null ? null : (string)value;
    }

    /// <summary>Calls gain until the session row carries the expected roots (the fetch is asynchronous).</summary>
    private async Task<string?> CallUntilRoots(McpClient client, string? expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Budget;
        string? seen;
        do
        {
            await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: ct);
            seen = RootsJson();
            if (seen == expected)
                return seen;
            await Task.Delay(50, ct);
        }
        while (DateTime.UtcNow < deadline);
        return seen;
    }

    private async Task RunHost(ClientCapabilities? capabilities, McpClientHandlers? handlers, Func<McpClient, CancellationToken, Task> body, StringWriter log)
    {
        using var cts = new CancellationTokenSource(Budget * 2);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var hostTask = McpHost.RunAsync(new McpServerOptions { Profile = "mcp-t5" },
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), log, Profiles, cts.Token);
        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
                Capabilities = capabilities,
                Handlers = handlers ?? new McpClientHandlers(),
            },
            NullLoggerFactory.Instance,
            cts.Token))
        {
            await body(client, cts.Token);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));
    }

    [Fact]
    public async Task Client_roots_reach_the_session_and_list_changed_overwrites_them()
    {
        var current = "file:///SQLH_ROOT_MARKER/one";
        var log = new StringWriter();
        await RunHost(
            new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } },
            new McpClientHandlers
            {
                RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult { Roots = [new Root { Uri = current, Name = "one" }] }),
            },
            async (client, ct) =>
            {
                Assert.Equal($$"""[{"uri":"{{current}}","name":"one"}]""", await CallUntilRoots(client, $$"""[{"uri":"{{current}}","name":"one"}]""", ct));

                current = "file:///SQLH_ROOT_MARKER/two";
                await client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification, ct);
                Assert.Equal($$"""[{"uri":"{{current}}","name":"one"}]""", await CallUntilRoots(client, $$"""[{"uri":"{{current}}","name":"one"}]""", ct));
            },
            log);

        Assert.DoesNotContain("SQLH_ROOT_MARKER", log.ToString());
    }

    [Fact]
    public async Task Client_without_roots_capability_is_never_asked()
    {
        var asked = 0;
        await RunHost(
            new ClientCapabilities(),
            new McpClientHandlers
            {
                RootsHandler = (_, _) => { Interlocked.Increment(ref asked); return ValueTask.FromResult(new ListRootsResult()); },
            },
            async (client, ct) =>
            {
                await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: ct);
                await client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification, ct);
                await Task.Delay(200, ct);
                await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: ct);
            },
            new StringWriter());

        Assert.Equal(0, asked);
        Assert.Null(RootsJson());
    }

    [Fact]
    public async Task List_changed_without_capability_is_ignored()
    {
        var asked = 0;
        await RunHost(
            new ClientCapabilities { Roots = new RootsCapability { ListChanged = false } },
            new McpClientHandlers
            {
                RootsHandler = (_, _) =>
                {
                    Interlocked.Increment(ref asked);
                    return ValueTask.FromResult(new ListRootsResult { Roots = [new Root { Uri = "file:///w" }] });
                },
            },
            async (client, ct) =>
            {
                Assert.Equal("""[{"uri":"file:///w"}]""", await CallUntilRoots(client, """[{"uri":"file:///w"}]""", ct));
                await client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification, ct);
                await Task.Delay(200, ct);
            },
            new StringWriter());

        Assert.Equal(1, asked);
    }
}
```

If the SDK client in 2.2.0 advertises `roots` automatically whenever `RootsHandler` is set, `Client_without_roots_capability_is_never_asked` would fail for the wrong reason; in that case drop `RootsHandler` from that test and keep the `asked` counter only on a handler-free client (assert `RootsJson()` is NULL).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpRootsTests"`
Expected: compile error (`McpRootsTracker` does not exist).

- [ ] **Step 3: Implement the tracker**

`src/SqlHarness.Mcp/McpRootsTracker.cs`:

```csharp
using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

namespace SqlHarness.Mcp;

/// <summary>
/// In-memory snapshot of the client's workspace roots. Fetches run in the background,
/// never block a tool call and never write the journal; the journal reads
/// <see cref="Current"/> at each operation start. A newer fetch cancels an older one,
/// and only the newest fetch may publish. Stderr carries counts and exception type
/// names only, never URIs or names.
/// </summary>
internal sealed class McpRootsTracker(TextWriter log, CancellationToken lifetime, TimeSpan? timeout = null)
{
    internal const int MaximumRoots = 32;
    internal const int MaximumUriLength = 2048;
    internal const int MaximumNameLength = 256;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private CancellationTokenSource? _newest;
    private string? _snapshot;

    public string? Current => Volatile.Read(ref _snapshot);

    public Task Refresh(Func<CancellationToken, ValueTask<ListRootsResult>> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationTokenSource fetch;
        lock (_gate)
        {
            _newest?.Cancel();
            fetch = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            fetch.CancelAfter(_timeout);
            _newest = fetch;
        }

        return Task.Run(() => FetchAsync(request, fetch));
    }

    private async Task FetchAsync(Func<CancellationToken, ValueTask<ListRootsResult>> request, CancellationTokenSource fetch)
    {
        try
        {
            var result = await request(fetch.Token);
            var (json, kept, total) = Normalize(result.Roots);
            lock (_gate)
            {
                if (!ReferenceEquals(_newest, fetch))
                    return;
                Volatile.Write(ref _snapshot, json);
            }

            if (kept < total)
                Write($"sqlharness-mcp: roots: kept {kept} of {total}.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || !IsNewest(fetch))
        {
            // Host shutdown or superseded by a newer fetch: nothing to report.
        }
        catch (OperationCanceledException)
        {
            Write("sqlharness-mcp: roots/list timed out; keeping the previous roots.");
        }
        catch (Exception exception)
        {
            Write($"sqlharness-mcp: roots/list failed ({exception.GetType().Name}); keeping the previous roots.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_newest, fetch))
                    _newest = null;
            }

            fetch.Dispose();
        }
    }

    private bool IsNewest(CancellationTokenSource fetch)
    {
        lock (_gate)
            return ReferenceEquals(_newest, fetch);
    }

    private void Write(string line)
    {
        lock (log)
            log.WriteLine(line);
    }

    internal static (string Json, int Kept, int Total) Normalize(IList<Root>? roots)
    {
        var total = roots?.Count ?? 0;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var root in (roots ?? []).Take(MaximumRoots))
            {
                writer.WriteStartObject();
                writer.WriteString("uri", Cap(root.Uri ?? string.Empty, MaximumUriLength));
                if (root.Name is { } name)
                    writer.WriteString("name", Cap(name, MaximumNameLength));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return (Encoding.UTF8.GetString(buffer.ToArray()), Math.Min(total, MaximumRoots), total);
    }

    private static string Cap(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
```

`Utf8JsonWriter` escapes non-ASCII by default (`\u017C`), which is still the same string after parsing; `Normalize_keeps_uris_verbatim` compares the parsed value, so it passes. Do not change the encoder.

- [ ] **Step 4: Wire the host**

In `McpHost.RunAsync`, after `lifetime` is created and before `process.DecorateModules(...)`,
register these notification handlers for handshake revisions. The `2026-07-28`
request-filter path remains conditional on the probe in "Post-upgrade
adjustments"; it has no `initialized` notification:

```csharp
        var roots = new McpRootsTracker(log, lifetime.Token);
        void RefreshRoots(bool listChanged)
        {
            // Roots are recorded only; they never widen --input-root.
            var server = running;
            var capability = server?.ClientCapabilities?.Roots;
            if (server is null || capability is null || (listChanged && capability.ListChanged != true))
                return;
            _ = roots.Refresh(token => server.RequestRootsAsync(new ListRootsRequestParams(), token));
        }

        serverOptions.Handlers.NotificationHandlers =
        [
            new(ModelContextProtocol.Protocol.NotificationMethods.InitializedNotification,
                (_, _) => { RefreshRoots(listChanged: false); return ValueTask.CompletedTask; }),
            new(ModelContextProtocol.Protocol.NotificationMethods.RootsListChangedNotification,
                (_, _) => { RefreshRoots(listChanged: true); return ValueTask.CompletedTask; }),
        ];
```

`ModelContextProtocol.Server.McpServer? running = null;` is currently declared after `sessionKey`/`mcpMode`; move its declaration above this block if needed so the local function can capture it. Then extend the decorator:

```csharp
        process.DecorateModules(module => new JournalingModule(
            module,
            () => journal.Value,
            identity,
            McpCallRegistry.Classify,
            () => roots.Current));
```

If `serverOptions.Handlers` is null in 2.2.0, initialize it first with `serverOptions.Handlers ??= new ModelContextProtocol.Server.McpServerHandlers();` (keep the line even if it is not null — it is harmless).

- [ ] **Step 5: Run the MCP tests**

Run: `dotnet test tests/SqlHarness.Mcp.Tests`
Expected: PASS. If a host test is flaky under load, rerun it alone (known pre-existing flakiness in MCP host handshake tests) before investigating.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Mcp/McpRootsTracker.cs src/SqlHarness.Mcp/McpHost.cs tests/SqlHarness.Mcp.Tests/McpRootsTests.cs
git commit -m "feat(mcp): record client workspace roots on MCP sessions"
```

---

### Task 6: Dashboard API fields

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardModels.cs` (`SessionSummary`, `OperationSummary`, new `SessionRoot`)
- Modify: `src/SqlHarness.Dashboard/JournalReader.cs` (`OperationColumns`, `SessionColumns`, `ReadSessionRow`, `ReadOperation`)
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs`, `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`

**Interfaces:**
- Consumes: v6 columns (Task 1).
- Produces (JSON camelCase): `SessionSummary.clientTitle: string|null`, `SessionSummary.roots: {uri: string, name: string|null}[] | null` (appended after `abandoned`); `OperationSummary.cancelReason: string|null` (appended after `errorMessage`).

- [ ] **Step 1: Write the failing tests**

`DashboardContractTests`: add `null` arguments for the new constructor parameters and extend the expected arrays:

```csharp
        var summary = new OperationSummary(1, 2, "claude", "query", "running", null, null, "s", "u", null, null, null, null,
            null, null, false, null, null, null, false, false, false, null, null, null);
        // expected names: ..., "progress", "errorMessage", "cancelReason"
```

```csharp
        var session = new SessionSummary(1, "k", "claude", "cli", "process-tree", null, null, null, null, "f", "l", 0, 0, 0, 0, 0, null, null);
        // expected names: ..., "running", "abandoned", "clientTitle", "roots"
```

`tests/SqlHarness.Tests/Dashboard/JournalSeed.cs` — let `Operation` seed an error kind and a cancel reason. Add two parameters at the end of the signature and use them in the `OperationEnd`:

```csharp
        string engine = "sqlserver", string server = "srv", string database = "db",
        string? errorKind = null, string? cancelReason = null)
```

```csharp
            _journal.Complete(handle, new OperationEnd(status, exitCode, errorKind ?? (status == "rejected" ? "safety" : null), durationMs,
                engine, server, database, 1, 3, tokens == SeedTokens.RawOnly ? 500 : null, null, null, CancelReason: cancelReason));
```

`JournalReaderTests` — add (uses the file's existing `Reader(home)` helper, `TempHome`, and `JournalSeed`):

```csharp
    [Fact]
    public void Session_and_operation_carry_title_roots_and_cancel_reason()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("mcp:r") with
        {
            ClientTitle = "Claude Code",
            RootsJson = """[{"uri":"file:///w","name":"w"},{"uri":"file:///x"}]""",
        };
        seed.Operation(session, status: "failed", exitCode: -1, errorKind: "cancelled", cancelReason: "client");

        var reader = Reader(home);
        var id = Assert.Single(reader.Sessions(new SessionQuery()).Items).Id;
        var detail = reader.Session(id)!;

        Assert.Equal("Claude Code", detail.Session.ClientTitle);
        Assert.Equal([new SessionRoot("file:///w", "w"), new SessionRoot("file:///x", null)], detail.Session.Roots);
        Assert.Equal("client", Assert.Single(detail.Operations).CancelReason);
    }

    [Fact]
    public void Pre_v6_rows_read_as_null()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:old"));

        var reader = Reader(home);
        var detail = reader.Session(Assert.Single(reader.Sessions(new SessionQuery()).Items).Id)!;

        Assert.Null(detail.Session.ClientTitle);
        Assert.Null(detail.Session.Roots);
        Assert.Null(Assert.Single(detail.Operations).CancelReason);
    }

    [Fact]
    public void Empty_and_malformed_roots_are_distinct()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("mcp:empty") with { RootsJson = "[]" });
        seed.Operation(JournalSeed.Session("mcp:bad") with { RootsJson = "not json" });

        var sessions = Reader(home).Sessions(new SessionQuery()).Items.ToDictionary(s => s.SessionKey);

        Assert.Empty(sessions["mcp:empty"].Roots!);
        Assert.Null(sessions["mcp:bad"].Roots);
    }
```

`SessionRoot` is a record, so the collection assertion compares by value.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: compile errors (new constructor arguments / properties missing).

- [ ] **Step 3: Implement**

`DashboardModels.cs`:

```csharp
public sealed record SessionRoot(string Uri, string? Name);

public sealed record SessionSummary(
    long Id, string SessionKey, string AgentKind, string Transport, string Source, string? ClientName,
    string? ClientVersion, string? McpMode, string? Cwd, string FirstSeen, string LastSeen,
    int Operations, int Failed, int Rejected, int Running, int Abandoned,
    string? ClientTitle, IReadOnlyList<SessionRoot>? Roots);

public sealed record OperationSummary(
    long Id, long SessionId, string AgentKind, string Operation, string Status, int? ExitCode, string? ErrorKind,
    string StartedAt, string UpdatedAt, string? FinishedAt, long? DurationMs, string? Profile, string? Engine,
    string? Server, string? Database, bool MutationRequested, string? SqlHash, long? RowsReturned,
    long? LogicalReadsMedian, bool HasSpill, bool ColdCache, bool OverGranted, JsonElement? Progress,
    string? ErrorMessage, string? CancelReason);
```

`JournalReader.cs` — append `o.cancel_reason` after `o.error_message` in `OperationColumns` (index 26), and `s.client_title, s.roots_json` at the end of `SessionColumns` (indexes 14, 15):

```csharp
    private const string SessionColumns = """
        s.id, s.session_key, s.agent_kind, s.transport, s.source, s.client_name, s.client_version, s.mcp_mode, s.cwd,
        s.first_seen, s.last_seen,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id) AS ops,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id AND o.status = 'failed') AS failed,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id AND o.status = 'rejected') AS rejected,
        s.client_title, s.roots_json
        """;
```

```csharp
    private static SessionSummary ReadSessionRow(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), NullableString(r, 5),
        NullableString(r, 6), NullableString(r, 7), NullableString(r, 8), r.GetString(9), r.GetString(10),
        r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), 0, 0,
        NullableString(r, 14), Roots(NullableString(r, 15)));
```

```csharp
        NullableString(r, 25), NullableString(r, 26));   // end of ReadOperation
```

```csharp
    private static IReadOnlyList<SessionRoot>? Roots(string? json)
    {
        if (json is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            return document.RootElement.EnumerateArray()
                .Select(item => new SessionRoot(
                    item.GetProperty("uri").GetString() ?? string.Empty,
                    item.TryGetProperty("name", out var name) ? name.GetString() : null))
                .ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
```

Search `JournalReader.cs` and the rest of `src/SqlHarness.Dashboard` for other `new SessionSummary(` / `new OperationSummary(` / `with { Running` call sites (for example `WithRunning`) and keep them compiling; `with` expressions need no change.

- [ ] **Step 4: Run the dashboard tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard/DashboardModels.cs src/SqlHarness.Dashboard/JournalReader.cs tests/SqlHarness.Tests/Dashboard tests/SqlHarness.Tests/Dashboard/JournalSeed.cs
git commit -m "feat(dashboard): expose client title, roots and cancel reason"
```

---

### Task 7: Dashboard UI

**Files:**
- Modify: `src/SqlHarness.Dashboard/ui/src/api/types.ts`
- Modify: `src/SqlHarness.Dashboard/ui/src/test/fixtures.ts`
- Create: `src/SqlHarness.Dashboard/ui/src/lib/roots.ts`, `src/SqlHarness.Dashboard/ui/src/lib/roots.test.ts`
- Modify: `src/SqlHarness.Dashboard/ui/src/lib/errors.ts`, `lib/errors.test.ts`
- Modify: `src/SqlHarness.Dashboard/ui/src/components/StatusBadge.tsx`, `components/StatusBadge.test.tsx`
- Modify: `src/SqlHarness.Dashboard/ui/src/components/SessionTable.tsx:33`
- Modify: `src/SqlHarness.Dashboard/ui/src/pages/SessionPage.tsx:47-49`, `pages/SessionPage.test.tsx`
- Modify: `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx:132`

**Interfaces:**
- Consumes: JSON fields from Task 6.
- Produces: `rootLabel(uri: string): string`, `clientLabel(session: Pick<SessionSummary, "clientTitle" | "clientName" | "clientVersion">): string | null`, `describeError(exitCode, errorKind, cancelReason?)`.

- [ ] **Step 1: Write the failing tests**

`lib/roots.test.ts`:

```ts
import { expect, test } from "vitest"
import { clientLabel, rootLabel } from "@/lib/roots"

test("file URIs show as local paths", () => {
  expect(rootLabel("file:///D:/Dev/sqlharness")).toBe("D:/Dev/sqlharness")
  expect(rootLabel("file:///home/me/work")).toBe("/home/me/work")
  expect(rootLabel("file:///D:/Projekty/Za%C5%BC%C3%B3%C5%82%C4%87%20g%C4%99%C5%9Bl%C4%85")).toBe("D:/Projekty/Zażółć gęślą")
})

test("other URIs and malformed escapes show raw", () => {
  expect(rootLabel("https://example.invalid/x")).toBe("https://example.invalid/x")
  expect(rootLabel("file:///bad%E0%A4%A")).toBe("file:///bad%E0%A4%A")
})

test("client label prefers the title", () => {
  expect(clientLabel({ clientTitle: "Claude Code", clientName: "claude-code", clientVersion: "2.1.0" })).toBe("Claude Code 2.1.0")
  expect(clientLabel({ clientTitle: null, clientName: "claude-code", clientVersion: null })).toBe("claude-code")
  expect(clientLabel({ clientTitle: null, clientName: null, clientVersion: "1" })).toBeNull()
})
```

`lib/errors.test.ts` — add:

```ts
test("cancellations name who cancelled", () => {
  expect(describeError(-1, "cancelled", "client")).toBe("Cancelled by the client.")
  expect(describeError(-1, "cancelled", "shutdown")).toBe("Cancelled because the MCP host shut down.")
  expect(describeError(-1, "cancelled", "deadline")).toBe("Cancelled when the call time budget ran out.")
  expect(describeError(-1, "cancelled", null)).toBe("The operation was cancelled before it finished.")
})
```

`pages/SessionPage.test.tsx` — add:

```ts
test("shows the client title and workspace roots", async () => {
  stubFetch({
    "/api/sessions/8": {
      session: session({ id: 8, transport: "mcp", clientName: "claude-code", clientVersion: "2.1.0", clientTitle: "Claude Code",
        roots: [{ uri: "file:///D:/Dev/sqlharness", name: "sqlharness" }, { uri: "file:///D:/Dev/other", name: null }] }),
      operations: [],
    },
  })
  renderApp("/sessions/8")
  expect(await screen.findByText("Claude Code 2.1.0")).toBeInTheDocument()
  expect(screen.getByText("D:/Dev/sqlharness")).toBeInTheDocument()
  expect(screen.getByText("(sqlharness)")).toBeInTheDocument()
  expect(screen.getByText("D:/Dev/other")).toBeInTheDocument()
})

test("unknown roots show a dash and an empty list shows none", async () => {
  stubFetch({
    "/api/sessions/9": { session: session({ id: 9, roots: null }), operations: [] },
    "/api/sessions/10": { session: session({ id: 10, roots: [] }), operations: [] },
  })
  renderApp("/sessions/9")
  expect(await screen.findByLabelText("Workspace roots")).toHaveTextContent("—")
  renderApp("/sessions/10")
  expect(await screen.findByText("none")).toBeInTheDocument()
})
```

Before writing the last test, open `components/Fact` (imported by `SessionPage.tsx`) to see how it renders a NULL value and whether it exposes a label; if `Fact` has no accessible label, assert on the rendered dash next to the "Workspace roots" text instead of `findByLabelText`.

`components/StatusBadge.test.tsx` — add a case rendering `operation({ status: "failed", exitCode: -1, errorKind: "cancelled", cancelReason: "client" })` and asserting the tooltip/dialog text contains "Cancelled by the client." (follow the interaction pattern already used in that file).

- [ ] **Step 2: Run to verify failure**

Run: `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: typecheck and test failures (missing fields, missing module `@/lib/roots`).

- [ ] **Step 3: Implement**

`api/types.ts` — in `SessionSummary` after `abandoned: number`:

```ts
  clientTitle: string | null
  /** null = unknown (client lacks roots or has not answered); [] = client reported none. */
  roots: SessionRoot[] | null
```

and `export type SessionRoot = { uri: string; name: string | null }`; in `OperationSummary` after `errorMessage`:

```ts
  /** "client" | "shutdown" | "deadline" when errorKind is "cancelled"; otherwise null. */
  cancelReason: string | null
```

`test/fixtures.ts` — add `clientTitle: null, roots: null` to `session(...)` defaults and `cancelReason: null` to `operation(...)` defaults.

`lib/roots.ts`:

```ts
import type { SessionSummary } from "@/api/types"

/** Local path for a file:// URI; any other or malformed URI is shown as received. */
export function rootLabel(uri: string): string {
  if (!uri.startsWith("file://")) return uri
  try {
    const path = decodeURIComponent(new URL(uri).pathname)
    return /^\/[A-Za-z]:/.test(path) ? path.slice(1) : path
  } catch {
    return uri
  }
}

export function clientLabel(session: Pick<SessionSummary, "clientTitle" | "clientName" | "clientVersion">): string | null {
  const name = session.clientTitle ?? session.clientName
  return name ? `${name} ${session.clientVersion ?? ""}`.trim() : null
}
```

`lib/errors.ts`:

```ts
const byCancelReason: Record<string, string> = {
  client: "Cancelled by the client.",
  shutdown: "Cancelled because the MCP host shut down.",
  deadline: "Cancelled when the call time budget ran out.",
}

/** A fixed, data-free sentence for an error kind; the stored message (if any) carries the details. */
export function describeError(exitCode: number | null, errorKind: string | null, cancelReason: string | null = null): string {
  if (errorKind === "cancelled" && cancelReason && byCancelReason[cancelReason]) return byCancelReason[cancelReason]
  return (errorKind && byKind[errorKind]) || (exitCode !== null && byExitCode[exitCode]) || byKind.operation_failed
}
```

`components/StatusBadge.tsx`:

```tsx
type StatusFields = Pick<OperationSummary, "status" | "exitCode" | "errorKind" | "errorMessage" | "cancelReason">
// ...
  const sentence = describeError(operation.exitCode, operation.errorKind, operation.cancelReason)
```

Callers that build a `StatusFields` object by hand (search for `<StatusBadge operation={{`) need `cancelReason` added; callers that pass a whole `OperationSummary` need nothing.

`components/SessionTable.tsx:33`:

```tsx
                {clientLabel(session) && <div className="text-sm text-muted-foreground">{clientLabel(session)}</div>}
```

`pages/SessionPage.tsx` — replace the Client fact and add the roots fact right after "Working directory":

```tsx
          <Fact label="Client" value={clientLabel(session)} />
          <Fact label="MCP mode" value={session.mcpMode} />
          <Fact label="Working directory" value={session.cwd} />
          <div className="md:col-span-2">
            <div className="text-sm text-muted-foreground">Workspace roots</div>
            {session.roots === null ? (
              <div aria-label="Workspace roots">—</div>
            ) : session.roots.length === 0 ? (
              <div aria-label="Workspace roots">none</div>
            ) : (
              <ul aria-label="Workspace roots" className="font-mono text-sm">
                {session.roots.map((root) => (
                  <li key={root.uri}>
                    <span>{rootLabel(root.uri)}</span>
                    {root.name && <span className="text-muted-foreground"> ({root.name})</span>}
                  </li>
                ))}
              </ul>
            )}
          </div>
```

Match the label/value markup `Fact` uses (open it) so the roots block looks like its neighbours; keep `aria-label="Workspace roots"` for the test. The `(sqlharness)` span must render as its own text node `(sqlharness)` for `getByText("(sqlharness)")`; render it as `{` (`}{root.name}{`)`}` inside one span if needed, i.e. `<span className="text-muted-foreground">({root.name})</span>` preceded by a plain space.

`pages/OperationPage.tsx:132` — show the reason next to the error kind:

```tsx
            <Fact label="Exit code">{op.exitCode ?? "—"}{op.errorKind ? ` (${op.errorKind}${op.cancelReason ? `: ${op.cancelReason}` : ""})` : ""}</Fact>
```

- [ ] **Step 4: Run the UI checks**

Run: `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: typecheck, lint and all vitest suites PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard/ui/src
git commit -m "feat(dashboard-ui): show client title, workspace roots and cancel reason"
```

---

### Task 8: Allowlisted `tools/call` metadata

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalModels.cs` (new `AgentCallMetadata`, `OperationStart.Agent`)
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs` (append to `Version6`)
- Modify: `src/SqlHarness.Core/Journal/ActivityJournal.cs` (`Begin` insert)
- Modify: `src/SqlHarness.Core/Journal/JournalingModule.cs` (third provider)
- Create: `src/SqlHarness.Mcp/McpCallMetadata.cs`
- Modify: `src/SqlHarness.Mcp/McpCallRegistry.cs` (carry metadata)
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`RunAsync` gets the request context; `RunDbAsync` passes metadata)
- Modify: `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs` (`TargetFreeAsync` / `RunAsync` get the request context)
- Modify: `src/SqlHarness.Mcp/McpHost.cs` (provider)
- Modify: `src/SqlHarness.Dashboard/DashboardModels.cs`, `src/SqlHarness.Dashboard/JournalReader.cs`
- Modify: `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `test/fixtures.ts`, `pages/OperationPage.tsx`, `components/OperationTable.tsx`
- Test: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, `tests/SqlHarness.Tests/Journal/JournalingModuleTests.cs`, new `tests/SqlHarness.Mcp.Tests/McpCallMetadataTests.cs`, `tests/SqlHarness.Mcp.Tests/McpJournalTests.cs`, `tests/SqlHarness.Tests/Dashboard/DashboardContractTests.cs`, `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `ui/src/pages/OperationPage.test.tsx`

**Interfaces:**
- Consumes: `McpCallRegistry.Track` / `Classify` (Task 4), `JournalingModule` constructor (Task 3), `Version6` (Task 1), `OperationSummary` / dashboard reader (Task 6).
- Produces:
  - Core: `public sealed record AgentCallMetadata(string? CallId, string? Model, string? ReasoningEffort, string? SessionId, string? TurnId, string? TurnTrigger, string? ThreadSource);` and `OperationStart(..., string? CandidateSqlText, AgentCallMetadata? Agent = null)`.
  - `JournalingModule(..., Func<CancellationToken, string?>? cancelReason = null, Func<string?>? rootsJson = null, Func<CancellationToken, AgentCallMetadata?>? callMetadata = null)`.
  - MCP: `McpCallMetadata.Read(JsonObject? meta) : AgentCallMetadata?`, `McpCallMetadata.MaximumLength = 256`; `McpCallRegistry.Track(CancellationTokenSource linked, CancellationToken request, CancellationToken shutdown, AgentCallMetadata? metadata = null)`; `McpCallRegistry.Metadata(CancellationToken moduleToken) : AgentCallMetadata?`.
  - Dashboard JSON: `OperationSummary.agentModel`, `OperationSummary.agentReasoningEffort` (appended after `cancelReason`); `OperationDetail.agent: AgentCall | null` (appended after `dimensions`) with `callId, model, reasoningEffort, sessionId, turnId, turnTrigger, threadSource`.

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Mcp.Tests/McpCallMetadataTests.cs`:

```csharp
using System.Text.Json.Nodes;

using SqlHarness.Core;

namespace SqlHarness.Mcp.Tests;

public sealed class McpCallMetadataTests
{
    [Fact]
    public void Claude_code_meta_yields_the_tool_use_id_only()
    {
        var meta = JsonNode.Parse("""{"claudecode/toolUseId":"toolu_0145","progressToken":2}""")!.AsObject();

        Assert.Equal(new AgentCallMetadata("toolu_0145", null, null, null, null, null, null), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Codex_meta_yields_the_allowlisted_turn_fields()
    {
        var meta = JsonNode.Parse("""
            {"callId":"exec-1","threadId":"t","sessionId":"s","windowId":"s:0","itemId":"ctc_1","progressToken":1,
             "x-codex-turn-metadata":{"session_id":"s","thread_id":"t","reasoning_effort":"low","turn_id":"turn-1",
               "model":"gpt-6-sol","thread_source":"user","turn_trigger":"exec","sandbox":"none",
               "sandbox_mode":"danger-full-access","auto_review_enabled":false,"turn_started_at_unix_ms":1791617819497,
               "codex_version":"0.160.0"}}
            """)!.AsObject();

        Assert.Equal(new AgentCallMetadata("exec-1", "gpt-6-sol", "low", "s", "turn-1", "exec", "user"), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Wrong_types_empty_strings_and_missing_keys_are_null()
    {
        var meta = JsonNode.Parse("""
            {"callId":"","x-codex-turn-metadata":{"model":42,"reasoning_effort":null,"turn_id":["x"],"session_id":"s"}}
            """)!.AsObject();

        Assert.Equal(new AgentCallMetadata(null, null, null, "s", null, null, null), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Turn_metadata_of_the_wrong_shape_keeps_the_call_id()
    {
        var meta = JsonNode.Parse("""{"callId":"exec-1","x-codex-turn-metadata":"not an object"}""")!.AsObject();

        Assert.Equal(new AgentCallMetadata("exec-1", null, null, null, null, null, null), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Nothing_allowlisted_or_no_meta_is_null()
    {
        Assert.Null(McpCallMetadata.Read(null));
        Assert.Null(McpCallMetadata.Read(JsonNode.Parse("""{"progressToken":3,"other":"x"}""")!.AsObject()));
    }

    [Fact]
    public void Long_values_are_cut()
    {
        var meta = new JsonObject { ["claudecode/toolUseId"] = new string('a', 1000) };

        Assert.Equal(McpCallMetadata.MaximumLength, McpCallMetadata.Read(meta)!.CallId!.Length);
    }

    [Fact]
    public void Registry_returns_metadata_while_the_call_is_tracked()
    {
        var metadata = new AgentCallMetadata("exec-1", "m", null, null, null, null, null);
        using var linked = new CancellationTokenSource();
        using (McpCallRegistry.Track(linked, CancellationToken.None, CancellationToken.None, metadata))
            Assert.Same(metadata, McpCallRegistry.Metadata(linked.Token));
        Assert.Null(McpCallRegistry.Metadata(linked.Token));
    }
}
```

`ActivityJournalTests` — add:

```csharp
    [Fact]
    public void Call_metadata_is_stored_with_the_start_row()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);
        var start = JournalTestData.Start() with
        {
            Agent = new AgentCallMetadata("exec-1", "gpt-6-sol", "low", "s", "turn-1", "exec", "user"),
        };

        journal.Begin(JournalTestData.Session(), start);

        var row = JournalDb.Rows(temp.DatabasePath, """
            SELECT client_call_id, agent_model, agent_reasoning_effort, agent_session_id, agent_turn_id,
                   agent_turn_trigger, agent_thread_source FROM operations
            """).Single();
        Assert.Equal(
            ["exec-1", "gpt-6-sol", "low", "s", "turn-1", "exec", "user"],
            row.Values.Cast<string>().ToArray());
    }
```

`JournalingModuleTests` — extend `Create` with `Func<CancellationToken, AgentCallMetadata?>? callMetadata = null` (pass it as the 6th constructor argument) and add:

```csharp
    [Fact]
    public async Task Call_metadata_provider_receives_the_module_token()
    {
        CancellationToken seen = default;
        using var cts = new CancellationTokenSource();
        var (module, temp) = Create(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            callMetadata: token => { seen = token; return new AgentCallMetadata("toolu_1", null, null, null, null, null, null); });
        using (temp)
        {
            await module.ExecuteAsync(Query(), cts.Token);

            Assert.Equal(cts.Token, seen);
            Assert.Equal("toolu_1", JournalDb.Rows(temp.DatabasePath, "SELECT client_call_id FROM operations").Single()["client_call_id"]);
        }
    }
```

`McpJournalTests` — add (same host setup as `Tool_call_records_session_from_client_info`, fixed mode only):

```csharp
    [Fact]
    public async Task Tool_call_meta_is_recorded_on_the_operation()
    {
        var options = new McpServerOptions { Profile = "mcp-t5" };
        Func<IReadOnlyDictionary<string, TargetProfile>> profiles = () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
        };
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var hostTask = McpHost.RunAsync(options, clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), new StringWriter(), profiles, cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "codex-mcp-client", Version = "0.160.0" }, ProtocolVersion = McpHost.FallbackProtocolVersion },
            NullLoggerFactory.Instance,
            cts.Token))
        {
            var meta = System.Text.Json.Nodes.JsonNode.Parse("""
                {"callId":"exec-9","x-codex-turn-metadata":{"model":"gpt-6-sol","reasoning_effort":"high","turn_id":"turn-9"}}
                """)!.AsObject();
            await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(),
                options: new ModelContextProtocol.RequestOptions { Meta = meta }, cancellationToken: cts.Token);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_home, "data", "activity.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_call_id, agent_model, agent_reasoning_effort, agent_turn_id FROM operations";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("exec-9", "gpt-6-sol", "high", "turn-9"), (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }
```

If `RequestOptions.Meta` is not a `JsonObject` in 2.2.0, construct the type it is (the server side reads `RequestParams.Meta`, which is `System.Text.Json.Nodes.JsonObject`).

Dashboard tests: in `DashboardContractTests` append `null, null` to the `OperationSummary` constructor and `"agentModel", "agentReasoningEffort"` to its expected names; add an `OperationDetail` names test only if one exists already (otherwise the reader test below covers `agent`). In `JournalReaderTests` add a seed overload or a direct `ActivityJournal` call that begins an operation with `Agent = new AgentCallMetadata("toolu_1", "m", "low", "s", "t", "exec", "user")`, then assert `reader.Operation(id, ...)!.Agent` equals `new AgentCall("toolu_1", "m", "low", "s", "t", "exec", "user")` and the summary's `AgentModel == "m"`; and that an operation without metadata has `Agent == null`. Use the existing `reader.Operation(...)` call shape from the neighbouring operation-detail tests in that file.

UI test in `pages/OperationPage.test.tsx`: render an operation detail fixture with `agent: { callId: "exec-1", model: "gpt-6-sol", reasoningEffort: "low", sessionId: "s", turnId: "turn-1", turnTrigger: "exec", threadSource: "user" }` and assert the "Agent call" card shows `gpt-6-sol`, `low`, `turn-1` and `exec-1`; render one with `agent: null` and assert no "Agent call" heading.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpCallMetadataTests"`
Expected: compile error (`McpCallMetadata`, `AgentCallMetadata` do not exist).

- [ ] **Step 3: Implement Core**

`JournalModels.cs`:

```csharp
/// <summary>Allowlisted, client-supplied labels from tools/call _meta; never verified, never content.</summary>
public sealed record AgentCallMetadata(
    string? CallId,
    string? Model,
    string? ReasoningEffort,
    string? SessionId,
    string? TurnId,
    string? TurnTrigger,
    string? ThreadSource);
```

and append `AgentCallMetadata? Agent = null` as the last `OperationStart` member.

`JournalSchema.Version6` — append:

```sql
        ALTER TABLE operations ADD COLUMN client_call_id TEXT;
        ALTER TABLE operations ADD COLUMN agent_model TEXT;
        ALTER TABLE operations ADD COLUMN agent_reasoning_effort TEXT;
        ALTER TABLE operations ADD COLUMN agent_session_id TEXT;
        ALTER TABLE operations ADD COLUMN agent_turn_id TEXT;
        ALTER TABLE operations ADD COLUMN agent_turn_trigger TEXT;
        ALTER TABLE operations ADD COLUMN agent_thread_source TEXT;
```

`ActivityJournal.Begin` insert:

```csharp
                INSERT INTO operations (session_id, operation, host_pid, host_started_at, started_at, updated_at,
                    status, profile, vars_json, mutation_requested, sql_hash, candidate_sql_hash, sql_text, candidate_sql_text,
                    client_call_id, agent_model, agent_reasoning_effort, agent_session_id, agent_turn_id, agent_turn_trigger, agent_thread_source)
                VALUES ($session, $operation, $hostPid, $hostStarted, $now, $now,
                    'running', $profile, $vars, $mutation, $sqlHash, $candidateHash, $sqlText, $candidateText,
                    $callId, $agentModel, $agentEffort, $agentSession, $agentTurn, $agentTrigger, $agentSource)
                RETURNING id;
```

```csharp
            insert.Parameters.AddWithValue("$callId", (object?)start.Agent?.CallId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentModel", (object?)start.Agent?.Model ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentEffort", (object?)start.Agent?.ReasoningEffort ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentSession", (object?)start.Agent?.SessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentTurn", (object?)start.Agent?.TurnId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentTrigger", (object?)start.Agent?.TurnTrigger ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentSource", (object?)start.Agent?.ThreadSource ?? DBNull.Value);
```

`JournalingModule.cs` — add the field and constructor parameter `Func<CancellationToken, AgentCallMetadata?>? callMetadata = null` (stored as `_callMetadata`), pass `ct` from `RunAsync` into `Begin(journal, operation, ct)`, and build the start there:

```csharp
            var start = OperationJournalDescriber.DescribeStart(operation);
            if (CallMetadata(ct) is { } agent)
                start = start with { Agent = agent };
            var current = _rootsJson is null ? session : session with { RootsJson = Roots() };
            return journal.Begin(current, start);
```

```csharp
    private AgentCallMetadata? CallMetadata(CancellationToken ct)
    {
        if (_callMetadata is null)
            return null;
        try
        {
            return _callMetadata(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }
```

- [ ] **Step 4: Implement MCP**

`src/SqlHarness.Mcp/McpCallMetadata.cs`:

```csharp
using System.Text.Json.Nodes;

using SqlHarness.Core;

namespace SqlHarness.Mcp;

/// <summary>
/// Allowlist over tools/call _meta. Claude Code sends claudecode/toolUseId; Codex sends
/// callId and an x-codex-turn-metadata object. Only string values are read, each capped;
/// every other key (sandbox mode, feature flags, window and item ids) is ignored.
/// </summary>
internal static class McpCallMetadata
{
    internal const int MaximumLength = 256;

    internal static AgentCallMetadata? Read(JsonObject? meta)
    {
        if (meta is null)
            return null;
        try
        {
            var turn = meta.TryGetPropertyValue("x-codex-turn-metadata", out var node) ? node as JsonObject : null;
            var result = new AgentCallMetadata(
                Text(meta, "claudecode/toolUseId") ?? Text(meta, "callId"),
                Text(turn, "model"),
                Text(turn, "reasoning_effort"),
                Text(turn, "session_id"),
                Text(turn, "turn_id"),
                Text(turn, "turn_trigger"),
                Text(turn, "thread_source"));
            return result == Empty ? null : result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly AgentCallMetadata Empty = new(null, null, null, null, null, null, null);

    private static string? Text(JsonObject? source, string key)
    {
        if (source is null || !source.TryGetPropertyValue(key, out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var text) || text.Length == 0)
            return null;
        return text.Length <= MaximumLength ? text : text[..MaximumLength];
    }
}
```

`McpCallRegistry.cs` — store the metadata with the call:

```csharp
    private static readonly ConcurrentDictionary<CancellationToken, (CancellationToken Request, CancellationToken Shutdown, AgentCallMetadata? Metadata)> Calls = new();

    internal static IDisposable Track(CancellationTokenSource linked, CancellationToken request, CancellationToken shutdown, AgentCallMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(linked);
        var token = linked.Token;
        Calls[token] = (request, shutdown, metadata);
        return new Registration(token);
    }

    internal static AgentCallMetadata? Metadata(CancellationToken moduleToken) =>
        Calls.TryGetValue(moduleToken, out var call) ? call.Metadata : null;
```

(`Classify` keeps reading `call.Shutdown` / `call.Request`.)

Run sites:
- `McpToolHandlers.RunDbAsync`: `using var reason = McpCallRegistry.Track(linked, ct, _hostShutdown, McpCallMetadata.Read(context?.Params?.Meta));`
- `McpToolHandlers.RunAsync`: change the signature to `RunAsync(string command, RequestContext<CallToolRequestParams>? context, Func<CancellationToken, Task<SqlHarnessOutcome>> run, CancellationToken ct)`, pass `ctx` from the five callers (`CapabilitiesAsync`, `ValidateAsync`, `PlanAsync`, `ArtifactAsync`, `GainAsync`), and track with `McpCallMetadata.Read(context?.Params?.Meta)`.
- `McpRequestToolHandlers`: add `RequestContext<CallToolRequestParams>? context` to `TargetFreeAsync` and `RunAsync` the same way; every caller has `ctx` as its first handler parameter.

`McpHost.cs` decorator:

```csharp
        process.DecorateModules(module => new JournalingModule(
            module,
            () => journal.Value,
            identity,
            McpCallRegistry.Classify,
            () => roots.Current,
            McpCallRegistry.Metadata));
```

- [ ] **Step 5: Implement dashboard**

`DashboardModels.cs`:

```csharp
public sealed record AgentCall(
    string? CallId, string? Model, string? ReasoningEffort, string? SessionId, string? TurnId,
    string? TurnTrigger, string? ThreadSource);
```

Append `string? AgentModel, string? AgentReasoningEffort` to `OperationSummary` (after `CancelReason`) and `AgentCall? Agent` to `OperationDetail` (after `Dimensions`).

`JournalReader.cs`: append `o.agent_model, o.agent_reasoning_effort` to `OperationColumns` (indexes 27, 28) and read them in `ReadOperation` with `NullableString(r, 27), NullableString(r, 28)`. In `Operation(...)`, extend the extra-columns query that already reads `vars_json`, hashes and texts with `client_call_id, agent_model, agent_reasoning_effort, agent_session_id, agent_turn_id, agent_turn_trigger, agent_thread_source`, build `AgentCall` from them, and pass `null` when all seven are NULL. Fix every other `new OperationSummary(` / `new OperationDetail(` call site the compiler reports.

UI: `types.ts` adds `agentModel: string | null`, `agentReasoningEffort: string | null` to `OperationSummary`, `export type AgentCall = { callId: string | null; model: string | null; reasoningEffort: string | null; sessionId: string | null; turnId: string | null; turnTrigger: string | null; threadSource: string | null }`, and `agent: AgentCall | null` to `OperationDetail`; `fixtures.ts` defaults them to `null`. `OperationPage.tsx` renders, when `detail.agent` is not null, a `Card` titled "Agent call" with `Fact`s "Model", "Effort", "Agent session", "Turn", "Trigger", "Thread source", "Call id" (same `Card`/`Fact` components the page already uses). `OperationTable.tsx` shows `agentModel` under the agent kind in the same muted style `SessionTable` uses for the client, only when it is not null.

- [ ] **Step 6: Run all affected suites**

Run: `dotnet test tests/SqlHarness.Tests` then `dotnet test tests/SqlHarness.Mcp.Tests` then `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(mcp): record allowlisted tools/call metadata (call id, Codex model and turn)"
```

---

### Task 9: Documentation and gates

**Files:**
- Modify: `AGENTS.md` (journal paragraph in "Safety contract", the bullet starting "Every operation that reaches the SQLHarness module")
- Modify: `docs/mcp.md` (section "Concurrency, deadline, cancellation, and progress", and a new short section after "Input roots and file inputs")

- [ ] **Step 1: Edit `AGENTS.md`**

In the journal bullet, after the sentence that begins "Session identity is implicit (MCP `clientInfo`, ...); agents send nothing extra.", insert:

```markdown
MCP sessions also record `clientInfo.title` and, when a `roots/list` request succeeds, the client's workspace roots (at most 32 roots, stored like `cwd`; client roots never widen `--input-root`). Handshake revisions request roots after `initialized` and refresh on `roots/list_changed`; `2026-07-28` support depends on the Task 5 request-scoped probe. A cancelled operation records `error_kind = cancelled` with `cancel_reason` `client`, `shutdown`, or `deadline`, including a cancellation Core reported as an SQL failure. Each operation also records allowlisted `tools/call._meta` labels: the client's call id (Claude Code `claudecode/toolUseId`, Codex `callId`) and, from Codex `x-codex-turn-metadata`, model, reasoning effort, agent session id, turn id, turn trigger and thread source; other `_meta` keys are never stored.
```

- [ ] **Step 2: Edit `docs/mcp.md`**

After the "Input roots and file inputs" section add:

```markdown
## Client workspace roots

On handshake revisions, when the client declares the `roots` capability,
SQLHarness requests `roots/list` after `initialized` and again on
`roots/list_changed` (only if the client declared `listChanged`). For
`2026-07-28`, state the Task 5 probe result here: either the first eligible
`tools/call` fetches roots through the request-scoped server, or roots remain
unknown (NULL) on that revision. A successful answer is recorded in the local
activity journal for the dashboard and nothing else: client roots never widen
`--input-root`, never authorize a file input, and never change a tool result.
A failed or slow (5 s) request keeps the previous snapshot; stderr names only
the failure class.
```

In "Concurrency, deadline, cancellation, and progress" append:

```markdown
The activity journal records why a call was cancelled: `client` (the client sent
`notifications/cancelled`), `shutdown` (stdin closed or the host stopped), or
`deadline` (the call time budget ran out).
```

- [ ] **Step 3: Run the gates, one after the other**

Run: `pwsh ./scripts/verify.ps1`
Expected: all stages green.

Run: `pwsh ./scripts/verify-linux.ps1`
Expected: all stages green. If it fails to sync from this worktree, stop and ask the user before applying any workaround (known limitation of running the Linux gate from a worktree).

- [ ] **Step 4: Commit**

```bash
git add AGENTS.md docs/mcp.md
git commit -m "docs: describe recorded MCP title, roots and cancel reason"
```
