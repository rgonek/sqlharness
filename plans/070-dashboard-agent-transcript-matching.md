# Plan 070: Dashboard agent transcript matching

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat 54dc621..HEAD -- src/SqlHarness.Core/Journal src/SqlHarness.Core/SqlHarnessConfig.cs src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard tests/SqlHarness.Tests/Journal README.md`. **Plans 062 and 069 must be DONE first** (069 adds `session_annotations`, `SessionSummary.Title/Starred/Archived/NotePreview`, `sessionDisplayName`, `SessionAnnotationHeader`, the annotation writer pattern). This plan uses journal schema **`Version10`**; if 10 is taken, use the next free number everywhere — adaptation, not a STOP. Positional records gain fields **at the end**, after whatever 062/069 appended; update `ReadSessionRow`/`ReadOperation` ordinals accordingly.
>
> **Re-verify the external formats before Task 3** (they are unversioned): run the inspection in Task 3 Step 0 against the operator's real `~/.claude` and `~/.codex` (keys and types only, never content). If a key moved, adapt the parser and its fixtures; record the observed shape in the commit message.

**Goal:** With an opt-in setting, the dashboard reads local Claude Code and Codex transcripts and attaches the agent's conversation title to sessions and Claude's model/effort to operations.

**Architecture:** A `TranscriptMatcher` in the dashboard runs one bounded, incremental cycle per housekeeping pass (every minute, like retention) while `dashboard.agentTranscripts.enabled` is true. Claude: it scans the project directory of each session `cwd` that still has unlinked operations, stages every SQLHarness `tool_use` (id, conversation, model, effort) and every `ai-title`, then links operations to staged calls by `client_call_id` (= Claude `toolUseId`). Codex: it reads `session_index.jsonl` and links by `agent_session_id`. Derived data lives in its own journal tables; `JournalReader` exposes the agent title, the conversations of a session and the effective model with its source.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, System.Text.Json, ASP.NET Core minimal APIs, xUnit; React 19, TypeScript, TanStack Query, Vitest.

**Spec:** [2026-10-14-dashboard-agent-transcript-matching-design.md](../docs/superpowers/specs/2026-10-14-dashboard-agent-transcript-matching-design.md)

## Status

- DONE on branch `feat/plan-070-agent-transcripts`; implementation and corrected Windows verification range `54dc621..64bbec5`. `just verify` passed at `64bbec5`; the operator explicitly waived Linux verification on 2026-10-15. Sol's final low-effort whole-branch review passed spec compliance and code quality with no critical or important findings; it recorded two minor test coverage gaps. Evidence: [Plan 070 verification](070-verification-evidence.md).
- Priority P2. Effort L. Risk MEDIUM (reads foreign, unversioned files; second dashboard writer into the journal; idle-shutdown accounting).

## Evidence (2026-10-14, keys/types only)

- Claude Code `_meta["claudecode/toolUseId"]` → `operations.client_call_id` (`McpCallMetadata.cs`); values start with `toolu_` (22 of 94 Claude operations; older ones lack it). Codex sends `callId` (`exec-…`) into the same column — **filter by `sessions.agent_kind`**.
- Claude transcripts: `~/.claude/projects/<cwd with every non [A-Za-z0-9] char replaced by '-'>/<sessionId>.jsonl` and subagent files `<sessionId>/subagents/agent-*.jsonl`. Assistant lines: top-level `type: "assistant"`, `sessionId`, `effort` (string, e.g. `"medium"`, may be null), `message.model`, `message.content[]` blocks `{type: "tool_use", id, name}`; SQLHarness tool names look like `mcp__<server>__sqlharness_<tool>`. Title lines: `{type: "ai-title", aiTitle, sessionId}` (1262 seen; no `custom-title` or `summary` type observed).
- Codex: `x-codex-turn-metadata.session_id` → `operations.agent_session_id` (+ model/effort already stored). `~/.codex/session_index.jsonl` lines `{id, thread_name, updated_at}`, appended on rename.
- Housekeeping (`DashboardHost.HousekeepAsync`) runs every `HousekeepingInterval` (1 min); idle shutdown watches `PRAGMA data_version`, so the matcher's own commits must be excluded like retention's.
- Stats do not use the agent model today (no model in `JournalReader.Stats`/`StatsPage`); the model shows in `OperationTable` and the operation's "Agent call" card. The spec's "Stats and workflow cost" item is realised by making `OperationSummary.agentModel` the effective model with a source, so any model-based view uses it.

## Global Constraints

- Setting `dashboard.agentTranscripts.enabled`, default `false`; strict config rules apply (unknown fields and explicit `null` invalid). Off ⇒ the matcher performs **zero** file-system reads of agent homes.
- Exact matching only: Claude by `client_call_id` with `sessions.agent_kind = 'claude'`; Codex by `agent_session_id` with `agent_kind = 'codex'`. No time-window or cwd-only heuristics.
- Only allowlisted fields are kept: conversation id, title, tool-use id, model, effort. Each capped at 256 characters (`McpCallMetadata.MaximumLength`). Nothing else from a transcript reaches memory beyond the current line, the journal, logs or the API.
- Agent homes: `CLAUDE_CONFIG_DIR` else `~/.claude`; `CODEX_HOME` else `~/.codex`. Never read `state_*.sqlite`.
- Files are opened with `FileShare.ReadWrite | FileShare.Delete`; a partial last line is not consumed; a shrunk file or an mtime that moved back restarts from offset 0.
- Read budget per cycle: 32 MiB (`TranscriptMatcher.CycleBudgetBytes`). Files whose mtime is older than the oldest pending operation of that directory minus 1 day are skipped.
- Derived data never overwrites `operations` columns or `session_annotations`. Display name: `annotation title ?? agent title ?? session key`. Model: `call ?? transcript`.
- Retention deletes links with operations and conversations nobody references. **Clear matched data** empties all four new tables.
- The matcher's journal commits are not activity for idle shutdown.
- No auto-formatting; touch only lines the change needs.
- Verification decision (2026-10-15): Windows `just verify` passed at `64bbec5`; the operator explicitly waived `just verify-linux`, which was not run and remains unverified. The branch-scoped formatter check passed for all 25 changed C# files.

## Review Focus

1. Claude writes the `tool_use` line **before** SQLHarness journals the operation, so a cycle can pass the line before the operation exists — staging must still link it on a later cycle (T4 test).
2. Codex `callId` values in `client_call_id` must never be looked up in Claude transcripts, and a Claude session must never link to a Codex thread (T4 test).
3. A transcript being appended while read: a line cut mid-way is neither parsed nor skipped; it is read whole next cycle (T3 test).
4. With the setting off, nothing under the agent homes is opened — not even a directory listing (T4 test with a throwing file system probe).
5. The matcher's commits do not keep a background dashboard alive past its idle limit (T5 test).

## File map

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/SqlHarnessConfig.cs` | `AgentTranscriptsConfig`, `DashboardConfig.AgentTranscripts`, completeness check |
| `src/SqlHarness.Core/Journal/JournalSchema.cs`, `ActivityJournal.cs` | `Version10` tables |
| `src/SqlHarness.Core/Journal/JournalRetention.cs` | orphan conversations |
| `src/SqlHarness.Dashboard/Transcripts/AgentHomes.cs` (new) | resolve Claude/Codex homes; Claude project dir encoding |
| `src/SqlHarness.Dashboard/Transcripts/TranscriptLines.cs` (new) | allowlisted line parsers (Claude, Codex index) |
| `src/SqlHarness.Dashboard/Transcripts/IncrementalTail.cs` (new) | cursor-based complete-line reader with budget |
| `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs` (new) | one cycle: discover, read, stage, link, prune; counters; clear |
| `src/SqlHarness.Dashboard/DashboardServer.cs`, `DashboardSecurity.cs` | matcher instance, status + clear endpoints |
| `src/SqlHarness.Dashboard/DashboardHost.cs` | run a cycle per housekeeping pass, version baseline |
| `src/SqlHarness.Dashboard/DashboardModels.cs`, `JournalReader.cs` | agent title, conversations, effective model |
| `ui/src/api/*`, `ui/src/lib/sessionName.ts`, `ui/src/components/*`, `ui/src/pages/{SessionPage,OperationPage,SettingsPage}.tsx` | UI |

---

### Task 1: Setting `dashboard.agentTranscripts.enabled`

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessConfig.cs`
- Test: `tests/SqlHarness.Tests/Journal/SqlHarnessConfigTests.cs`

**Interfaces:**
- Produces: `public sealed record AgentTranscriptsConfig { public bool Enabled { get; init; } }`; `DashboardConfig.AgentTranscripts` (`AgentTranscriptsConfig`, default `new()`); JSON `{"dashboard":{"agentTranscripts":{"enabled":true}}}`.

- [ ] **Step 1: Failing tests** (append; follow the file's helpers for parsing bytes/strings):

```csharp
[Fact]
public void Agent_transcripts_default_off_and_round_trip()
{
    Assert.False(SqlHarnessConfig.Default.Dashboard.AgentTranscripts.Enabled);
    var parsed = SqlHarnessConfigLoader.Parse("""{"dashboard":{"agentTranscripts":{"enabled":true}}}"""u8.ToArray());
    Assert.True(parsed.Config!.Dashboard.AgentTranscripts.Enabled);
    var again = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(SqlHarnessConfigWriter.Serialize(parsed.Config)));
    Assert.True(again.Config!.Dashboard.AgentTranscripts.Enabled);
}

[Theory]
[InlineData("""{"dashboard":{"agentTranscripts":null}}""")]
[InlineData("""{"dashboard":{"agentTranscripts":{"enabled":"yes"}}}""")]
[InlineData("""{"dashboard":{"agentTranscripts":{"enabled":true,"paths":[]}}}""")]
public void Invalid_agent_transcripts_section_is_rejected(string json)
{
    Assert.Null(SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json)).Config);
}
```

- [ ] **Step 2: RED** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlHarnessConfigTests"`.

- [ ] **Step 3: Implement.**

```csharp
public sealed record AgentTranscriptsConfig
{
    /// <summary>Read local Claude/Codex transcripts for titles, model and effort. Off by default.</summary>
    public bool Enabled { get; init; }
}
```

Add `public AgentTranscriptsConfig AgentTranscripts { get; init; } = new();` to `DashboardConfig`. Extend `IsComplete` with `&& config.Dashboard.AgentTranscripts is not null` and the failure text to "The journal, journal.retention, dashboard and dashboard.agentTranscripts sections must be objects."

- [ ] **Step 4: GREEN** — same filter plus `FullyQualifiedName~DashboardSettingsTests`. Existing settings tests that compare whole serialized documents get the new section.

- [ ] **Step 5: Commit** — `git commit -m "feat(config): dashboard.agentTranscripts.enabled setting"` (stage the two files).

---

### Task 2: Journal schema v10 and retention

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs`, `ActivityJournal.cs` (`Migrate`), `JournalRetention.cs` (`DeleteOrphans`)
- Test: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, `JournalRetentionTests.cs`

**Interfaces:**
- Produces tables:

```sql
CREATE TABLE agent_conversations (
    id INTEGER PRIMARY KEY,
    agent_kind TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    title TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE (agent_kind, conversation_id)
);
CREATE TABLE agent_tool_calls (           -- staging: SQLHarness tool uses seen in transcripts, not yet linked
    agent_kind TEXT NOT NULL,
    call_id TEXT NOT NULL,
    conversation INTEGER NOT NULL REFERENCES agent_conversations(id) ON DELETE CASCADE,
    model TEXT,
    effort TEXT,
    seen_at TEXT NOT NULL,
    PRIMARY KEY (agent_kind, call_id)
);
CREATE TABLE operation_agent_links (
    operation_id INTEGER PRIMARY KEY REFERENCES operations(id) ON DELETE CASCADE,
    conversation INTEGER NOT NULL REFERENCES agent_conversations(id) ON DELETE CASCADE,
    model TEXT,
    effort TEXT
);
CREATE INDEX ix_operation_agent_links_conversation ON operation_agent_links(conversation);
CREATE TABLE transcript_cursors (
    path TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    mtime TEXT NOT NULL,
    offset INTEGER NOT NULL
);
```

- [ ] **Step 1: Failing tests.** Migration test as in Plan 069 Task 1 (previous schema at `user_version = 9` → `CurrentVersion == 10`, four new empty tables, data kept). Retention test:

```csharp
[Fact]
public void Retention_removes_links_with_operations_and_unreferenced_conversations()
{
    using var temp = new JournalTempDirectory();
    Seed(temp, Now.AddDays(-40), key: "cli:old");
    Seed(temp, Now.AddDays(-1), key: "cli:new");
    using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_conversations (id, agent_kind, conversation_id, title, updated_at) VALUES
                (1, 'claude', 'old-conv', 'old', 't'), (2, 'claude', 'new-conv', 'new', 't'), (3, 'claude', 'staged', null, 't');
            INSERT INTO operation_agent_links (operation_id, conversation, model) SELECT id, CASE WHEN started_at < '2026-09-20' THEN 1 ELSE 2 END, 'm' FROM operations;
            INSERT INTO agent_tool_calls (agent_kind, call_id, conversation, seen_at) VALUES ('claude', 'toolu_x', 3, 't');
            """;
        command.ExecuteNonQuery();
    }

    JournalRetention.Run(temp.DatabasePath, Config(maxAgeDays: 30), new Processes(), new FixedTimeProvider(Now));

    Assert.Equal(["new-conv", "staged"],
        JournalDb.Rows(temp.DatabasePath, "SELECT conversation_id FROM agent_conversations ORDER BY conversation_id").Select(r => (string)r["conversation_id"]!));
    Assert.Equal(1, Count(temp, "operation_agent_links"));
}
```

(`Seed` timestamps come from `Now.AddDays(...)`; check the stored `started_at` format and adjust the CASE literal if needed.)

- [ ] **Step 2: RED** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ActivityJournalTests|FullyQualifiedName~JournalRetentionTests"`.

- [ ] **Step 3: Implement.** `CurrentVersion = 10`; `Version10` = the DDL above; `if (locked < 10) Execute(connection, JournalSchema.Version10);`. In `JournalRetention.DeleteOrphans`, after the sessions batch:

```csharp
        DeleteInBatches(connection, null, _ => { }, $"""
            DELETE FROM agent_conversations WHERE id IN (
                SELECT id FROM agent_conversations c
                WHERE NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.conversation = c.id)
                  AND NOT EXISTS (SELECT 1 FROM agent_tool_calls t WHERE t.conversation = c.id)
                LIMIT {BatchSize});
            """);
```

(Check `DeleteInBatches` binds `$cutoff`; pass `null` like the plans batch.) Fix literal version pins in older migration tests to `JournalSchema.CurrentVersion`.

- [ ] **Step 4: GREEN** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal|FullyQualifiedName~Dashboard"`.

- [ ] **Step 5: Commit** — `git commit -m "feat(journal): agent transcript tables (schema v10)"`.

---

### Task 3: Agent homes, line parsers and incremental tail

**Files:**
- Create: `src/SqlHarness.Dashboard/Transcripts/AgentHomes.cs`, `TranscriptLines.cs`, `IncrementalTail.cs`
- Test: create `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptLinesTests.cs`, `IncrementalTailTests.cs`, `AgentHomesTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed record AgentHomes(string ClaudeHome, string CodexHome)` with `static AgentHomes FromEnvironment(Func<string, string?> env, string userHome)` and `string ClaudeProjectDirectory(string cwd)`.
  - `internal static class TranscriptLines` with
    - `ClaudeLine? ParseClaude(ReadOnlySpan<byte> line)` returning `internal sealed record ClaudeLine(string ConversationId, string? AiTitle, string? Model, string? Effort, IReadOnlyList<string> SqlHarnessToolUseIds)`; null for lines that carry neither a title nor a SQLHarness tool use, or are malformed.
    - `CodexIndexEntry? ParseCodexIndex(ReadOnlySpan<byte> line)` returning `internal sealed record CodexIndexEntry(string Id, string? Title)`.
  - `internal sealed record TailCursor(long Size, DateTimeOffset Mtime, long Offset)`;
    `internal static class IncrementalTail` with `TailRead Read(string path, TailCursor? cursor, long budget, Action<ReadOnlySpan<byte>> onLine)` returning `internal sealed record TailRead(TailCursor Cursor, long BytesRead, bool Completed)`.

- [ ] **Step 0: Re-verify formats** (operator machine, keys/types only):

```bash
python -I - <<'EOF'
import json,glob,os,collections
c=collections.Counter()
for f in glob.glob(os.path.expanduser('~/.claude/projects/*/*.jsonl'))[:50]:
    for l in open(f,encoding='utf-8'):
        try:o=json.loads(l)
        except: continue
        if o.get('type') in ('ai-title','assistant'):
            c[(o['type'],tuple(sorted(k for k in o if k in('aiTitle','sessionId','effort','message'))))]+=1
print(c)
print(open(os.path.expanduser('~/.codex/session_index.jsonl'),encoding='utf-8').readline().split('"')[1::2][::2])
EOF
```

Expected: `('ai-title', ('aiTitle','sessionId'))`, `('assistant', ('effort','message','sessionId'))`, Codex keys `id, thread_name, updated_at`. If different, adapt the parsers and fixtures below.

- [ ] **Step 1: Failing tests.** `AgentHomesTests`:

```csharp
[Theory]
[InlineData(@"C:\BSI\Tools\sqlharness", "C--BSI-Tools-sqlharness")]
[InlineData(@"C:\Users\robert.gonek\.notes", "C--Users-robert-gonek--notes")]
[InlineData("/home/dev/my_repo", "-home-dev-my-repo")]
public void Claude_project_directory_replaces_every_non_alphanumeric(string cwd, string expected)
{
    var homes = new AgentHomes("/claude", "/codex");
    Assert.Equal(Path.Combine("/claude", "projects", expected), homes.ClaudeProjectDirectory(cwd));
}

[Fact]
public void Homes_honour_environment_overrides()
{
    var homes = AgentHomes.FromEnvironment(name => name switch { "CLAUDE_CONFIG_DIR" => "/c", "CODEX_HOME" => "/x", _ => null }, "/u");
    Assert.Equal(new AgentHomes("/c", "/x"), homes);
    Assert.Equal(new AgentHomes(Path.Combine("/u", ".claude"), Path.Combine("/u", ".codex")), AgentHomes.FromEnvironment(_ => null, "/u"));
}
```

`TranscriptLinesTests`:

```csharp
private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

[Fact]
public void Assistant_line_yields_only_sqlharness_tool_uses_with_model_and_effort()
{
    var line = ClaudeFixtures.Assistant("conv-1", "claude-opus-5-5", "medium",
        ("toolu_1", "mcp__bsi-sqlharness-uat__sqlharness_query"), ("toolu_2", "Bash"), ("toolu_3", "mcp__sqlharness__sqlharness_compare"));

    var parsed = TranscriptLines.ParseClaude(B(line))!;

    Assert.Equal(("conv-1", "claude-opus-5-5", "medium"), (parsed.ConversationId, parsed.Model, parsed.Effort));
    Assert.Equal(["toolu_1", "toolu_3"], parsed.SqlHarnessToolUseIds);
    Assert.DoesNotContain("secret prompt", parsed.ToString());
}

[Fact]
public void Ai_title_line_yields_the_title()
{
    var parsed = TranscriptLines.ParseClaude(B("""{"type":"ai-title","aiTitle":"Optimize orders query","sessionId":"conv-1"}"""))!;
    Assert.Equal(("conv-1", "Optimize orders query"), (parsed.ConversationId, parsed.AiTitle));
}

[Theory]
[InlineData("""{"type":"user","sessionId":"c","message":{"content":"secret prompt"}}""")]
[InlineData("""{"type":"assistant","sessionId":"c","message":{"model":"m","content":[{"type":"text","text":"hi"}]}}""")]
[InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"mcp__s__sqlharness_query"}]}}""")]
[InlineData("""{"type":"ai-title","aiTitle":42,"sessionId":"c"}""")]
[InlineData("not json")]
[InlineData("[1,2]")]
public void Irrelevant_or_malformed_lines_yield_null(string line) => Assert.Null(TranscriptLines.ParseClaude(B(line)));

[Fact]
public void Values_are_capped()
{
    var parsed = TranscriptLines.ParseClaude(B($$"""{"type":"ai-title","aiTitle":"{{new string('a', 1000)}}","sessionId":"c"}"""))!;
    Assert.Equal(256, parsed.AiTitle!.Length);
}

[Fact]
public void Codex_index_entry()
{
    Assert.Equal(new CodexIndexEntry("019a", "Fix orders"), TranscriptLines.ParseCodexIndex(B("""{"id":"019a","thread_name":"Fix orders","updated_at":"2026-10-14T10:00:00Z"}""")));
    Assert.Null(TranscriptLines.ParseCodexIndex(B("""{"thread_name":"x"}""")));
}
```

with a test helper `ClaudeFixtures` (in the same test folder) that builds an assistant line including a decoy text block containing `"secret prompt"`:

```csharp
internal static class ClaudeFixtures
{
    internal static string Assistant(string conversation, string model, string? effort, params (string Id, string Name)[] tools)
    {
        var blocks = new List<object> { new { type = "text", text = "secret prompt" } };
        blocks.AddRange(tools.Select(t => (object)new { type = "tool_use", id = t.Id, name = t.Name, input = new { sql = "SELECT secret" } }));
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant", sessionId = conversation, effort, cwd = "/work",
            message = new { model, role = "assistant", content = blocks },
        });
    }

    internal static string Title(string conversation, string title) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = "ai-title", aiTitle = title, sessionId = conversation });
}
```

`IncrementalTailTests`:

```csharp
[Fact]
public void Reads_only_complete_lines_and_resumes_from_the_cursor()
{
    using var dir = new TempHome();
    var path = Path.Combine(dir.Path, "t.jsonl");
    File.WriteAllText(path, "a\nb\npart");
    var lines = new List<string>();

    var first = IncrementalTail.Read(path, null, 1024, l => lines.Add(System.Text.Encoding.UTF8.GetString(l)));
    File.AppendAllText(path, "ial\nc\n");
    var second = IncrementalTail.Read(path, first.Cursor, 1024, l => lines.Add(System.Text.Encoding.UTF8.GetString(l)));

    Assert.Equal(["a", "b", "partial", "c"], lines);
    Assert.Equal(4, first.Cursor.Offset);
    Assert.True(second.Completed);
}

[Fact]
public void Unchanged_file_is_not_read_and_a_shrunk_file_restarts()
{
    using var dir = new TempHome();
    var path = Path.Combine(dir.Path, "t.jsonl");
    File.WriteAllText(path, "a\nb\n");
    var first = IncrementalTail.Read(path, null, 1024, _ => { });
    Assert.Equal(0, IncrementalTail.Read(path, first.Cursor, 1024, _ => { }).BytesRead);

    File.WriteAllText(path, "z\n");
    var lines = new List<string>();
    IncrementalTail.Read(path, first.Cursor, 1024, l => lines.Add(System.Text.Encoding.UTF8.GetString(l)));
    Assert.Equal(["z"], lines);
}

[Fact]
public void Budget_stops_at_a_line_boundary_and_reports_incomplete()
{
    using var dir = new TempHome();
    var path = Path.Combine(dir.Path, "t.jsonl");
    File.WriteAllText(path, "aaaa\nbbbb\ncccc\n");
    var lines = new List<string>();

    var read = IncrementalTail.Read(path, null, 6, l => lines.Add(System.Text.Encoding.UTF8.GetString(l)));

    Assert.False(read.Completed);
    Assert.Equal(["aaaa"], lines);
    Assert.Equal(5, read.Cursor.Offset);
}

[Fact]
public void A_file_open_for_writing_by_another_process_can_be_read()
{
    using var dir = new TempHome();
    var path = Path.Combine(dir.Path, "t.jsonl");
    using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    writer.Write("x\n"u8);
    writer.Flush();
    var lines = new List<string>();
    IncrementalTail.Read(path, null, 1024, l => lines.Add(System.Text.Encoding.UTF8.GetString(l)));
    Assert.Equal(["x"], lines);
}
```

- [ ] **Step 2: RED** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Transcripts"`.

- [ ] **Step 3: Implement `AgentHomes.cs`:**

```csharp
using System.Text.RegularExpressions;

namespace SqlHarness.Dashboard.Transcripts;

/// <summary>Where Claude Code and Codex keep their transcripts; only read when the setting is on.</summary>
internal sealed partial record AgentHomes(string ClaudeHome, string CodexHome)
{
    internal static AgentHomes FromEnvironment(Func<string, string?> env, string userHome) => new(
        NonEmpty(env("CLAUDE_CONFIG_DIR")) ?? Path.Combine(userHome, ".claude"),
        NonEmpty(env("CODEX_HOME")) ?? Path.Combine(userHome, ".codex"));

    internal static AgentHomes Current() =>
        FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Claude Code names a project directory after the cwd with every non-alphanumeric character replaced by '-'.</summary>
    internal string ClaudeProjectDirectory(string cwd) => Path.Combine(ClaudeHome, "projects", NonAlphanumeric().Replace(cwd, "-"));

    internal string CodexSessionIndex => Path.Combine(CodexHome, "session_index.jsonl");

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
```

- [ ] **Step 4: Implement `TranscriptLines.cs`** with `Utf8JsonReader`-free `JsonDocument` per line (lines are small enough; the document is disposed before returning). Cheap prefilter avoids parsing most lines:

```csharp
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Dashboard.Transcripts;

internal sealed record ClaudeLine(string ConversationId, string? AiTitle, string? Model, string? Effort, IReadOnlyList<string> SqlHarnessToolUseIds);

internal sealed record CodexIndexEntry(string Id, string? Title);

/// <summary>
/// Allowlist parsers for one transcript line. Only ids, titles, model and effort leave this class;
/// prompts, responses and tool inputs/results are never copied.
/// </summary>
internal static class TranscriptLines
{
    internal const int MaximumLength = 256;
    private static readonly byte[] AiTitleMarker = "\"ai-title\""u8.ToArray();
    private static readonly byte[] SqlHarnessToolMarker = "__sqlharness_"u8.ToArray();

    internal static ClaudeLine? ParseClaude(ReadOnlySpan<byte> line)
    {
        var isTitle = line.IndexOf(AiTitleMarker) >= 0;
        if (!isTitle && line.IndexOf(SqlHarnessToolMarker) < 0)
            return null;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "sessionId") is not { } conversation)
                return null;
            var type = Text(root, "type");
            if (type == "ai-title")
                return Text(root, "aiTitle") is { } title ? new ClaudeLine(conversation, title, null, null, []) : null;
            if (type != "assistant" || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return null;
            var ids = new List<string>();
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object && Text(block, "type") == "tool_use"
                        && Text(block, "name") is { } name && IsSqlHarnessTool(name) && Text(block, "id") is { } id)
                        ids.Add(id);
                }
            }
            return ids.Count == 0 ? null : new ClaudeLine(conversation, null, Text(message, "model"), Text(root, "effort"), ids);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static CodexIndexEntry? ParseCodexIndex(ReadOnlySpan<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && Text(root, "id") is { } id ? new CodexIndexEntry(id, Text(root, "thread_name")) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>MCP tools reach Claude as mcp__&lt;server&gt;__&lt;tool&gt;; SQLHarness tools are named sqlharness_*.</summary>
    private static bool IsSqlHarnessTool(string name) =>
        name.StartsWith("mcp__", StringComparison.Ordinal) && name.Contains("__sqlharness_", StringComparison.Ordinal);

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? SafeTruncation.Cap(text, MaximumLength)
            : null;
}
```

(`SafeTruncation` is in `SqlHarness.Core` and used by `McpCallMetadata`; if it is `internal` to Core, check `InternalsVisibleTo` for the dashboard assembly and add it, or copy a 3-line cap that does not split a surrogate pair.)

- [ ] **Step 5: Implement `IncrementalTail.cs`:**

```csharp
namespace SqlHarness.Dashboard.Transcripts;

internal sealed record TailCursor(long Size, DateTimeOffset Mtime, long Offset);

internal sealed record TailRead(TailCursor Cursor, long BytesRead, bool Completed);

/// <summary>
/// Reads complete '\n'-terminated lines appended since the cursor, up to a byte budget, without
/// blocking the writer. A trailing partial line stays unread; a shrunk or older file restarts at 0.
/// </summary>
internal static class IncrementalTail
{
    private const int ChunkBytes = 64 * 1024;

    internal static TailRead Read(string path, TailCursor? cursor, long budget, Action<ReadOnlySpan<byte>> onLine)
    {
        var info = new FileInfo(path);
        var size = info.Length;
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var offset = cursor is null || size < cursor.Offset || mtime < cursor.Mtime ? 0 : cursor.Offset;
        if (cursor is not null && offset == cursor.Offset && size == cursor.Size && mtime == cursor.Mtime)
            return new TailRead(cursor, 0, true);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        var pending = new MemoryStream();
        var buffer = new byte[ChunkBytes];
        long read = 0;
        var consumed = offset;
        while (read < budget)
        {
            var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, budget - read));
            if (n == 0)
                break;
            read += n;
            var span = buffer.AsSpan(0, n);
            int newline;
            while ((newline = span.IndexOf((byte)'\n')) >= 0)
            {
                ReadOnlySpan<byte> line;
                if (pending.Length > 0)
                {
                    pending.Write(span[..newline]);
                    line = pending.GetBuffer().AsSpan(0, (int)pending.Length);
                }
                else
                {
                    line = span[..newline];
                }
                consumed += (pending.Length > 0 ? pending.Length : newline) + 1;
                onLine(line.TrimEnd((byte)'\r'));
                pending.SetLength(0);
                span = span[(newline + 1)..];
            }
            pending.Write(span);
        }

        var completed = consumed + pending.Length >= size;
        return new TailRead(new TailCursor(size, mtime, consumed), read, completed);
    }
}
```

Note the subtle `consumed` arithmetic: when `pending` holds the start of a line, the line length is `pending.Length` after appending the head; verify with the tests and simplify if a reviewer finds it unclear (e.g. track `lineStart` absolute offsets instead).

- [ ] **Step 6: GREEN** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Transcripts"`.

- [ ] **Step 7: Commit** — `git commit -m "feat(dashboard): allowlisted transcript parsers and incremental tail"`.

---

### Task 4: TranscriptMatcher cycle

**Files:**
- Create: `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs`
- Test: create `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs`

**Interfaces:**
- Consumes: Task 2 tables, Task 3 types.
- Produces:
  - `internal sealed record TranscriptStatus(bool Enabled, int Matched, int Unmatched, int ReadErrors, string? LastCycleAt)`.
  - `internal sealed class TranscriptMatcher(string databasePath, AgentHomes homes, TimeProvider time)` with
    - `const long CycleBudgetBytes = 32L * 1024 * 1024`;
    - `void RunCycle(bool enabled)` — never throws; does nothing (no I/O at all) when `enabled` is false;
    - `TranscriptStatus Status(bool enabled)` — counts from the journal + last-cycle counters;
    - `bool Clear()` — empties the four tables in one transaction; false when busy or schema mismatch.
  - Internal seam for tests: `internal Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;` (the off-test replaces it with a throwing probe).

- [ ] **Step 1: Failing tests.** Use `TempHome` for the journal and a separate temp dir as the agent home; seed operations with `JournalSeed.Operation(session, agent: new AgentCallMetadata(callId, model, effort, sessionId, null, null, null))` — check `AgentCallMetadata`'s positional order in `JournalModels.cs` (`CallId, Model, ReasoningEffort, SessionId, TurnId, TurnTrigger, ThreadSource`) and `JournalSeed.Session(key, agent)` sets `agent_kind`. Sessions need `cwd`; `JournalSeed.Session` uses `/work`.

```csharp
public sealed class TranscriptMatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IDisposable
    {
        public TempHome Home { get; } = new();
        public TempHome Agents { get; } = new();
        public AgentHomes Homes => new(Path.Combine(Agents.Path, "claude"), Path.Combine(Agents.Path, "codex"));
        public JournalSeed Seed { get; }
        public Fixture() => Seed = new JournalSeed(Home.DatabasePath);
        public TranscriptMatcher Matcher() => new(Home.DatabasePath, Homes, new Journal.FixedTimeProvider(Now));

        public string ClaudeFile(string conversation, params string[] lines)
        {
            var dir = Homes.ClaudeProjectDirectory("/work");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, conversation + ".jsonl");
            File.AppendAllLines(path, lines);
            return path;
        }

        public void CodexIndex(params string[] lines)
        {
            Directory.CreateDirectory(Homes.CodexHome);
            File.AppendAllLines(Homes.CodexSessionIndex, lines);
        }

        public List<Dictionary<string, object?>> Links() => Journal.JournalDb.Rows(Home.DatabasePath, """
            SELECT o.client_call_id, c.agent_kind, c.conversation_id, c.title, l.model, l.effort
            FROM operation_agent_links l JOIN operations o ON o.id = l.operation_id JOIN agent_conversations c ON c.id = l.conversation
            ORDER BY o.id
            """);

        public void Dispose() { Home.Dispose(); Agents.Dispose(); }
    }

    private static AgentCallMetadata Call(string? callId = null, string? sessionId = null, string? model = null) =>
        new(callId, model, null, sessionId, null, null, null);

    [Fact]
    public void Claude_operation_links_by_tool_use_id_with_model_effort_and_title()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_1"));
        f.ClaudeFile("conv-1",
            ClaudeFixtures.Title("conv-1", "Optimize orders"),
            ClaudeFixtures.Assistant("conv-1", "claude-opus-5-5", "high", ("toolu_1", "mcp__s__sqlharness_query")));

        f.Matcher().RunCycle(enabled: true);

        var link = Assert.Single(f.Links());
        Assert.Equal(("claude", "conv-1", "Optimize orders", "claude-opus-5-5", "high"),
            ((string)link["agent_kind"]!, (string)link["conversation_id"]!, (string?)link["title"], (string?)link["model"], (string?)link["effort"]));
    }

    [Fact]
    public void A_tool_use_read_before_the_operation_exists_links_on_a_later_cycle()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_0"));   // makes /work pending
        f.ClaudeFile("conv-1",
            ClaudeFixtures.Assistant("conv-1", "m", null, ("toolu_0", "mcp__s__sqlharness_query")),
            ClaudeFixtures.Assistant("conv-1", "m", null, ("toolu_2", "mcp__s__sqlharness_measure")));
        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);

        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_2"));
        matcher.RunCycle(enabled: true);

        Assert.Equal(["toolu_0", "toolu_2"], f.Links().Select(r => (string)r["client_call_id"]!));
    }

    [Fact]
    public void Codex_call_ids_are_never_looked_up_in_claude_transcripts()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:x", "codex"), agent: Call("toolu_1"));
        f.ClaudeFile("conv-1", ClaudeFixtures.Assistant("conv-1", "m", null, ("toolu_1", "mcp__s__sqlharness_query")));

        f.Matcher().RunCycle(enabled: true);

        Assert.Empty(f.Links());
    }

    [Fact]
    public void Codex_operation_links_by_session_id_and_follows_a_rename()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:x", "codex"), agent: Call("exec-1", sessionId: "019a", model: "gpt"));
        f.CodexIndex("""{"id":"019a","thread_name":"First name","updated_at":"t"}""", """{"id":"other","thread_name":"Not ours","updated_at":"t"}""");
        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);
        f.CodexIndex("""{"id":"019a","thread_name":"Renamed","updated_at":"t2"}""");
        matcher.RunCycle(enabled: true);

        var link = Assert.Single(f.Links());
        Assert.Equal(("codex", "019a", "Renamed"), ((string)link["agent_kind"]!, (string)link["conversation_id"]!, (string?)link["title"]));
        Assert.Null(link["model"]);
        Assert.Empty(Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT 1 FROM agent_conversations WHERE conversation_id = 'other'"));
    }

    [Fact]
    public void Subagent_transcripts_are_scanned_and_one_session_can_link_several_conversations()
    {
        using var f = new Fixture();
        var session = JournalSeed.Session("mcp:c", "claude");
        f.Seed.Operation(session, agent: Call("toolu_a"));
        f.Seed.Operation(session, agent: Call("toolu_b"));
        f.ClaudeFile("conv-1", ClaudeFixtures.Assistant("conv-1", "m", null, ("toolu_a", "mcp__s__sqlharness_query")));
        var sub = Path.Combine(f.Homes.ClaudeProjectDirectory("/work"), "conv-2", "subagents");
        Directory.CreateDirectory(sub);
        File.WriteAllLines(Path.Combine(sub, "agent-1.jsonl"), [ClaudeFixtures.Assistant("conv-2", "m", null, ("toolu_b", "mcp__s__sqlharness_query"))]);

        f.Matcher().RunCycle(enabled: true);

        Assert.Equal(["conv-1", "conv-2"], f.Links().Select(r => (string)r["conversation_id"]!));
    }

    [Fact]
    public void Disabled_matcher_touches_no_agent_files()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_1"));
        var matcher = new TranscriptMatcher(f.Home.DatabasePath, f.Homes, new Journal.FixedTimeProvider(Now))
        {
            DirectoryExists = _ => throw new InvalidOperationException("agent home probed while disabled"),
        };

        matcher.RunCycle(enabled: false);

        Assert.Empty(f.Links());
        Assert.False(matcher.Status(enabled: false).Enabled);
    }

    [Fact]
    public void Missing_homes_and_garbage_lines_are_counted_not_thrown()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_1"));
        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);                 // no claude/codex home at all
        f.ClaudeFile("conv-1", "not json", "{\"type\":\"assistant\"");
        matcher.RunCycle(enabled: true);

        var status = matcher.Status(enabled: true);
        Assert.Equal((0, 1), (status.Matched, status.Unmatched));
        Assert.NotNull(status.LastCycleAt);
    }

    [Fact]
    public void Clear_removes_all_derived_rows_and_rescans_from_the_start()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_1"));
        f.ClaudeFile("conv-1", ClaudeFixtures.Assistant("conv-1", "m", null, ("toolu_1", "mcp__s__sqlharness_query")));
        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);

        Assert.True(matcher.Clear());
        foreach (var table in new[] { "operation_agent_links", "agent_tool_calls", "agent_conversations", "transcript_cursors" })
            Assert.Empty(Journal.JournalDb.Rows(f.Home.DatabasePath, $"SELECT 1 FROM {table}"));

        matcher.RunCycle(enabled: true);
        Assert.Single(f.Links());
    }

    [Fact]
    public void Nothing_but_allowlisted_fields_is_stored()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_1"));
        f.ClaudeFile("conv-1", ClaudeFixtures.Assistant("conv-1", "m", null, ("toolu_1", "mcp__s__sqlharness_query")));

        f.Matcher().RunCycle(enabled: true);

        var dump = string.Join("|", new[] { "agent_conversations", "agent_tool_calls", "operation_agent_links", "transcript_cursors" }
            .SelectMany(t => Journal.JournalDb.Rows(f.Home.DatabasePath, $"SELECT * FROM {t}"))
            .SelectMany(r => r.Values).Select(v => v?.ToString()));
        Assert.DoesNotContain("secret", dump);
    }

    [Fact]
    public void Old_transcript_files_are_skipped_for_new_operations()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_1"));
        var old = f.ClaudeFile("old", ClaudeFixtures.Assistant("old", "m", null, ("toolu_1", "mcp__s__sqlharness_query")));
        File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        f.Matcher().RunCycle(enabled: true);

        Assert.Empty(f.Links());
    }
}
```

(The seeded operations get `started_at` from `JournalSeed`'s clock, 2026-10-07; a file mtime of "now" is newer, a 2020 mtime is older than oldest-pending minus 1 day.)

- [ ] **Step 2: RED** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcherTests"`.

- [ ] **Step 3: Implement `TranscriptMatcher.cs`.** Structure (keep each method short; one `SqliteConnection` per cycle; file reading happens **outside** write transactions; all journal writes of a cycle in one `BEGIN IMMEDIATE` batch at the end):

```csharp
using System.Globalization;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard.Transcripts;

internal sealed record TranscriptStatus(bool Enabled, int Matched, int Unmatched, int ReadErrors, string? LastCycleAt);

/// <summary>
/// Opt-in, bounded, incremental matching of journal operations to local agent transcripts.
/// One cycle: find directories/threads with unlinked identifiable operations, read appended
/// transcript lines within a byte budget, stage SQLHarness tool uses and titles, link, prune.
/// Never throws; nothing is read while disabled.
/// </summary>
internal sealed class TranscriptMatcher(string databasePath, AgentHomes homes, TimeProvider time)
{
    internal const long CycleBudgetBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan MtimeSlack = TimeSpan.FromDays(1);
    private static readonly TimeSpan StagingLifetime = TimeSpan.FromDays(7);

    private int _readErrors;
    private string? _lastCycleAt;

    internal Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;

    internal void RunCycle(bool enabled)
    {
        if (!enabled)
            return;
        _readErrors = 0;
        try
        {
            using var connection = Open();
            if (connection is null || !SchemaMatches(connection))
                return;
            var batch = new Batch();
            var budget = CycleBudgetBytes;
            budget = ScanClaude(connection, batch, budget);
            ScanCodex(connection, batch, budget);
            Commit(connection, batch);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            _readErrors++;
        }
        finally
        {
            _lastCycleAt = Timestamp(time.GetUtcNow());
        }
    }
    // ... (methods below)
}
```

Implement these members (code shapes are normative; names are used by tests and Task 5):

1. `Open()` — `SqliteConnection` like `SessionAnnotationWriter` (Plan 069): `Mode = ReadWrite`, `Pooling = false`, `DefaultTimeout = 1`, `PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON;`; null when the database file does not exist.
2. `SchemaMatches(connection)` — `PRAGMA user_version == JournalSchema.CurrentVersion`.
3. `sealed class Batch` — collects `Conversations` (`Dictionary<(string Kind, string Id), string?>` title; a later title overwrites an earlier one, `null` never overwrites a title), `ToolCalls` (`List<(string CallId, string Conversation, string? Model, string? Effort)>`), `Cursors` (`Dictionary<string, TailCursor>`).
4. `ScanClaude(connection, batch, budget)`:
   - Pending directories:
     ```sql
     SELECT s.cwd, MIN(o.started_at) FROM operations o JOIN sessions s ON s.id = o.session_id
     WHERE s.agent_kind = 'claude' AND o.client_call_id IS NOT NULL AND s.cwd IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id)
     GROUP BY s.cwd;
     ```
   - For each `cwd`: `dir = homes.ClaudeProjectDirectory(cwd)`; skip when `!DirectoryExists(dir)`. Files: `Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories)` (top-level conversations and `<id>/subagents/*.jsonl`), each file once; skip paths with a `memory` directory segment. Skip files with `LastWriteTimeUtc < oldestPending - MtimeSlack`.
   - Load each file's cursor (`SELECT size, mtime, offset FROM transcript_cursors WHERE path = $p`), `IncrementalTail.Read(path, cursor, budget, line => Collect(batch, TranscriptLines.ParseClaude(line)))`, store the new cursor in `batch.Cursors`, subtract `BytesRead`; stop when the budget is exhausted. Catch `IOException`/`UnauthorizedAccessException` per file → `_readErrors++`, continue.
   - `Collect`: for a title line add/overwrite `("claude", id) → title`; for tool uses add the conversation (title unknown → keep existing) and each `(callId, conversation, model, effort)`.
5. `ScanCodex(connection, batch, budget)`:
   - Wanted ids: `SELECT DISTINCT o.agent_session_id FROM operations o JOIN sessions s ON s.id = o.session_id WHERE s.agent_kind = 'codex' AND o.agent_session_id IS NOT NULL` (all, not only unlinked — a rename must update the title). Return early when empty or the index file is missing (`File.Exists(homes.CodexSessionIndex)`; this is the only Codex probe).
   - Tail the index with its cursor; for each entry whose `Id` is wanted, set `("codex", Id) → Title` (latest line wins).
6. `Commit(connection, batch)` — one `BEGIN IMMEDIATE` … `COMMIT`:
   ```sql
   -- per conversation
   INSERT INTO agent_conversations (agent_kind, conversation_id, title, updated_at) VALUES ($kind, $id, $title, $now)
   ON CONFLICT(agent_kind, conversation_id) DO UPDATE SET title = COALESCE(excluded.title, title), updated_at = $now;
   -- per tool call
   INSERT INTO agent_tool_calls (agent_kind, call_id, conversation, model, effort, seen_at)
   SELECT 'claude', $call, c.id, $model, $effort, $now FROM agent_conversations c WHERE c.agent_kind = 'claude' AND c.conversation_id = $conv
   ON CONFLICT(agent_kind, call_id) DO NOTHING;
   -- per cursor
   INSERT INTO transcript_cursors (path, size, mtime, offset) VALUES ($path, $size, $mtime, $offset)
   ON CONFLICT(path) DO UPDATE SET size = excluded.size, mtime = excluded.mtime, offset = excluded.offset;
   -- link Claude
   INSERT INTO operation_agent_links (operation_id, conversation, model, effort)
   SELECT o.id, t.conversation, t.model, t.effort
   FROM operations o JOIN sessions s ON s.id = o.session_id
   JOIN agent_tool_calls t ON t.agent_kind = 'claude' AND t.call_id = o.client_call_id
   WHERE s.agent_kind = 'claude' AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id);
   -- link Codex
   INSERT INTO operation_agent_links (operation_id, conversation)
   SELECT o.id, c.id
   FROM operations o JOIN sessions s ON s.id = o.session_id
   JOIN agent_conversations c ON c.agent_kind = 'codex' AND c.conversation_id = o.agent_session_id
   WHERE s.agent_kind = 'codex' AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id);
   -- prune staging: linked calls, and unlinked calls older than 7 days
   DELETE FROM agent_tool_calls WHERE EXISTS (
       SELECT 1 FROM operations o JOIN operation_agent_links l ON l.operation_id = o.id WHERE o.client_call_id = agent_tool_calls.call_id)
     OR seen_at < $stagingCutoff;
   ```
   Timestamps use `Timestamp(DateTimeOffset)` = `UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)`; cursor `mtime` uses the round-trip format `"O"`.
   Pruning linked calls keeps staging small. A conversation whose only staged call was pruned and that has no link is removed by retention (Task 2).
7. `Status(bool enabled)`:
   ```sql
   SELECT
     (SELECT COUNT(*) FROM operation_agent_links),
     (SELECT COUNT(*) FROM operations o JOIN sessions s ON s.id = o.session_id
      WHERE ((s.agent_kind = 'claude' AND o.client_call_id IS NOT NULL) OR (s.agent_kind = 'codex' AND o.agent_session_id IS NOT NULL))
        AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id));
   ```
   Opened read-only (reuse `Open()` is fine; no writes). Returns zeros when the database is missing.
8. `Clear()` — one `BEGIN IMMEDIATE`: `DELETE FROM operation_agent_links; DELETE FROM agent_tool_calls; DELETE FROM agent_conversations; DELETE FROM transcript_cursors;` — false on `SqliteException` busy/locked or schema mismatch.

- [ ] **Step 4: GREEN** — Step 2 filter; then `FullyQualifiedName~Dashboard`.

- [ ] **Step 5: Commit** — `git commit -m "feat(dashboard): incremental transcript matcher for Claude and Codex"`.

---

### Task 5: Run cycles from housekeeping; status and clear endpoints

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs` (options, `RunningDashboard`, routes), `src/SqlHarness.Dashboard/DashboardSecurity.cs`, `src/SqlHarness.Dashboard/DashboardHost.cs` (`HousekeepAsync`)
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`, `tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs`

**Interfaces:**
- Consumes: Task 4 matcher.
- Produces:
  - `DashboardServerOptions.AgentHomes` (`AgentHomes`, default `AgentHomes.Current()`), `RunningDashboard.Transcripts` (`internal TranscriptMatcher`).
  - `GET /api/transcripts` → `TranscriptStatus` (reads `dashboard.agentTranscripts.enabled` from `options.ConfigPath` per request).
  - `POST /api/transcripts/clear` (guarded write) → 200 `TranscriptStatus` or 503 `{ "error": "The journal is busy; try again." }`.

- [ ] **Step 1: Failing tests.**
  - Server: `Transcript_status_reports_the_setting_and_clear_requires_the_write_protections` — write `config.json` with the setting on (use `SqlHarnessConfigWriter.Write`), `GET /api/transcripts` → `enabled: true`; `POST /api/transcripts/clear` without the header → 403; with header and JSON body `{}` → 200. Update `Only_get_endpoints_and_the_three_guarded_writes_exist` (from 069) to four writes including `POST /api/transcripts/clear`.
  - Housekeeping (follow the existing `DashboardHousekeepingTests` pattern for running the host with a fake clock, `IdleShutdown`, `Housekept`): (a) with the setting on and a Claude transcript in a temp `AgentHomes`, one pass links the operation; (b) with idle shutdown 2 minutes and the setting on, a host with no requests and no agent writes still exits after the idle period although each pass commits matcher rows (append a new transcript line before each pass so the matcher really writes); (c) with the setting off and a directory probe that throws, passes run without exception and create no links.
  Add `internal AgentHomes? AgentHomes { get; init; }` and `internal Func<string, bool>? AgentDirectoryProbe { get; init; }` to `DashboardHostOptions`; `DashboardHost` passes both, plus `Time = options.Time`, into `DashboardServerOptions`.

- [ ] **Step 2: RED** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardServerTests|FullyQualifiedName~DashboardHousekeepingTests"`.

- [ ] **Step 3: Server.** In `DashboardServerOptions`: `internal AgentHomes AgentHomes { get; init; } = AgentHomes.Current();` and `internal Func<string, bool>? AgentDirectoryProbe { get; init; }` (test seam). In `Build`, create `var transcripts = new TranscriptMatcher(options.DatabasePath, options.AgentHomes, options.Time) { DirectoryExists = options.AgentDirectoryProbe ?? Directory.Exists };` (`options.Time` from Plan 069), expose it on `RunningDashboard` (pass through the constructor). Routes:

```csharp
        MapRead(api, "/transcripts", () => Results.Json(transcripts.Status(TranscriptsEnabled(options.ConfigPath)), Json));
        api.MapMethods("/transcripts/clear", [HttpMethods.Post], (Delegate)(() =>
            transcripts.Clear()
                ? Results.Json(transcripts.Status(TranscriptsEnabled(options.ConfigPath)), Json)
                : Results.Json(new { error = "The journal is busy; try again." }, Json, statusCode: StatusCodes.Status503ServiceUnavailable)));
```

```csharp
    private static bool TranscriptsEnabled(string configPath) =>
        SqlHarnessConfigLoader.Load(configPath).Config.Dashboard.AgentTranscripts.Enabled;
```

`DashboardSecurity`: `internal const string TranscriptsClearPath = "/api/transcripts/clear";` and `isTranscriptsClear = IsPost && path == TranscriptsClearPath` added to `isWrite`; update the class summary.

- [ ] **Step 4: Host.** Pass `running` into `HousekeepAsync`. In each pass, after the retention block and **before** `var version = ReadVersion();`:

```csharp
                var transcriptsEnabled = (options.ConfigPath is null ? options.Config.Config : SqlHarnessConfigLoader.Load(options.ConfigPath).Config)
                    .Dashboard.AgentTranscripts.Enabled;
                if (transcriptsEnabled)
                {
                    // Count agent commits since the last check before the matcher's own commits hide them.
                    var beforeMatching = ReadVersion();
                    if (beforeMatching is not null && lastVersion is not null && beforeMatching != lastVersion)
                        lastJournalChange = now;
                    await Task.Run(() => running.Transcripts.RunCycle(enabled: true), ct);
                    // The matcher's commits are not activity: baseline after it.
                    lastVersion = ReadVersion();
                }
```

Load the config once per pass and reuse it for the retention block (refactor the existing `journal` line to read from the same loaded config) — touch only those lines.

- [ ] **Step 5: GREEN** — Step 2 filter, then `FullyQualifiedName~Dashboard`.

- [ ] **Step 6: Commit** — `git commit -m "feat(dashboard): run transcript matching in housekeeping; status and clear endpoints"`.

---

### Task 6: Reader — agent title, conversations, effective model

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardModels.cs`, `src/SqlHarness.Dashboard/JournalReader.cs`
- Test: `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `DashboardContractTests.cs`

**Interfaces:**
- Produces:
  - `SessionSummary` appends `string? AgentTitle` — title of the session's most recently linked conversation that has a title.
  - `SessionDetail` appends `IReadOnlyList<AgentConversation> Conversations`; `public sealed record AgentConversation(string AgentKind, string ConversationId, string? Title, int Operations);` ordered by the latest linked operation, newest first.
  - `OperationSummary.AgentModel` / `AgentReasoningEffort` become effective values (`call ?? transcript`); `OperationSummary` appends `string? AgentModelSource` (`"call"`, `"transcript"` or null).
  - `AgentCall` appends `string? TranscriptModel, string? TranscriptReasoningEffort, string? ConversationTitle`.
  - Session search `q` (069) also matches the agent title.

- [ ] **Step 1: Failing tests** (seed links with raw SQL like 069's `JournalSeed.Annotate`; add `JournalSeed.Link(databasePath, operationId, kind, conversationId, title, model, effort)`):
  - `Agent_title_comes_from_the_newest_linked_conversation_with_a_title` — two conversations linked to two operations of one session; the newer one titled → `AgentTitle`; then the newer untitled → older's title.
  - `Session_detail_lists_its_conversations_newest_first_with_operation_counts`.
  - `Effective_model_prefers_the_call_and_reports_its_source` — op A with call model `gpt` and a link model `x` → `("gpt","call")`; op B without call model, link `claude-opus-5-5` → `("claude-opus-5-5","transcript")`; op C neither → `(null,null)`.
  - `Operation_detail_carries_transcript_model_and_conversation_title`.
  - `Search_matches_the_agent_title`.
  - Contract test: append the new names (`agentTitle`; `agentModelSource`; `transcriptModel`, `transcriptReasoningEffort`, `conversationTitle`) and a new `AgentConversation` names test (`agentKind`, `conversationId`, `title`, `operations`).

- [ ] **Step 2: RED** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~JournalReaderTests|FullyQualifiedName~DashboardContractTests"`.

- [ ] **Step 3: Implement.**
  - `SessionColumns` append:
    ```sql
    , (SELECT c.title FROM operation_agent_links l
         JOIN agent_conversations c ON c.id = l.conversation
         JOIN operations o2 ON o2.id = l.operation_id
       WHERE o2.session_id = s.id AND c.title IS NOT NULL
       ORDER BY o2.id DESC LIMIT 1) AS agent_title
    ```
  - `OperationColumns`: replace `o.agent_model, o.agent_reasoning_effort` with
    ```sql
    COALESCE(o.agent_model, (SELECT l.model FROM operation_agent_links l WHERE l.operation_id = o.id)) AS agent_model,
    COALESCE(o.agent_reasoning_effort, (SELECT l.effort FROM operation_agent_links l WHERE l.operation_id = o.id)) AS agent_reasoning_effort
    ```
    and append `CASE WHEN o.agent_model IS NOT NULL THEN 'call' WHEN EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id AND l.model IS NOT NULL) THEN 'transcript' END AS agent_model_source` at the end; read it in `ReadOperation` at the new last ordinal. Keep column positions of everything else.
  - `Session(id)`:
    ```sql
    SELECT c.agent_kind, c.conversation_id, c.title, COUNT(*) FROM operation_agent_links l
    JOIN agent_conversations c ON c.id = l.conversation JOIN operations o ON o.id = l.operation_id
    WHERE o.session_id = $id GROUP BY c.id ORDER BY MAX(o.id) DESC;
    ```
  - `Operation(id)` extra query: `LEFT JOIN operation_agent_links l ON l.operation_id = operations.id LEFT JOIN agent_conversations c ON c.id = l.conversation` and read `l.model, l.effort, c.title` into `AgentCall` (`ReadAgent` gets the three new values; the call's own `Model`/`ReasoningEffort` stay the raw call values).
  - Search predicate (069) gains `OR EXISTS (SELECT 1 FROM operation_agent_links l JOIN agent_conversations c ON c.id = l.conversation JOIN operations o3 ON o3.id = l.operation_id WHERE o3.session_id = s.id AND c.title LIKE $q ESCAPE '\')`.

- [ ] **Step 4: GREEN** — Step 2 filter plus `FullyQualifiedName~LiveFeedTests|FullyQualifiedName~Stats`.

- [ ] **Step 5: Commit** — `git commit -m "feat(dashboard): agent titles, conversations and effective model in the read model"`.

---

### Task 7: UI — titles, conversations, model source, settings

**Files:**
- Modify: `ui/src/api/types.ts`, `ui/src/api/queries.ts`, `ui/src/lib/sessionName.ts` (+ test), `ui/src/components/SessionAnnotationHeader.tsx`, `ui/src/components/OperationTable.tsx`, `ui/src/pages/SessionPage.tsx`, `ui/src/pages/OperationPage.tsx`, `ui/src/pages/SettingsPage.tsx`, `ui/src/test/fixtures.ts`
- Create: `ui/src/components/SessionConversations.tsx`
- Test: `ui/src/lib/sessionName.test.ts`, `ui/src/pages/{SessionPage,OperationPage,SettingsPage,SessionsPage}.test.tsx`

**Interfaces:**
- Consumes: Task 5 endpoints, Task 6 fields.
- Produces: `sessionDisplayName(s: Pick<SessionSummary, "title" | "agentTitle" | "sessionKey">)` = `title ?? agentTitle ?? sessionKey`; `agentTitleSource(kind: string): string` → `"from Claude"` / `"from Codex"` / `"from agent"`; `useTranscriptStatus()`, `useClearTranscripts()`; `TranscriptStatus` TS type `{ enabled: boolean; matched: number; unmatched: number; readErrors: number; lastCycleAt: string | null }`; `Settings.dashboard.agentTranscripts: { enabled: boolean }`.

- [ ] **Step 1: Fixture defaults**: `session()` adds `agentTitle: null`; operation fixture adds `agentModelSource: null`; detail fixtures add `conversations: []`; agent call fixtures add the three transcript fields `null`; settings fixtures add `agentTranscripts: { enabled: false }`.

- [ ] **Step 2: Failing tests.**
  - `sessionName.test.ts`: user title wins; agent title next; key last.
  - `SessionsPage.test.tsx`: a session with only `agentTitle: "Optimize orders"` shows that text as the link.
  - `SessionPage.test.tsx`: the title editor's placeholder is the agent title and the text `from Claude` is visible next to the heading when `title` is null and `agentTitle` is set; a `conversations` list renders title, agent label and `2 operations`; clearing the user title shows the agent title as heading.
  - `OperationPage.test.tsx`: an operation whose `agent.model` is null and `agent.transcriptModel` is `claude-opus-5-5` shows `claude-opus-5-5` with `from transcript`.
  - `SettingsPage.test.tsx`: toggle "Read agent transcripts" saves `dashboard.agentTranscripts.enabled: true` via the existing PUT; the card shows `Matched 3 · Unmatched 1 · Read errors 0` from `/api/transcripts`; "Clear matched data" asks for confirmation in a dialog and then POSTs `/api/transcripts/clear` with the write header.

- [ ] **Step 3: RED** — `npm run test -- src/lib/sessionName.test.ts src/pages/SessionsPage.test.tsx src/pages/SessionPage.test.tsx src/pages/OperationPage.test.tsx src/pages/SettingsPage.test.tsx`.

- [ ] **Step 4: Implement.**
  - `types.ts`: new fields/types from **Interfaces**.
  - `queries.ts`:
    ```ts
    export function useTranscriptStatus() {
      return useQuery({ queryKey: ["transcripts"], queryFn: () => getJson<TranscriptStatus>("/api/transcripts") })
    }

    export function useClearTranscripts() {
      const client = useQueryClient()
      return useMutation({
        mutationFn: () => postJson<TranscriptStatus>("/api/transcripts/clear", {}),
        onSuccess: data => {
          client.setQueryData(["transcripts"], data)
          void client.invalidateQueries({ queryKey: ["sessions"] })
          void client.invalidateQueries({ queryKey: ["session"] })
        },
      })
    }
    ```
    `postJson` maps 502 to the server message; extend it to also read `{ error }` for 503 (or use `RequestError` from 069 for any non-OK JSON error).
  - `sessionName.ts`: the new rule; `agentTitleSource`.
  - `SessionAnnotationHeader`: heading `session.title ?? session.agentTitle ?? \`Session ${session.id}\``; when `title` is null and `agentTitle` is set, a muted `<span>{agentTitleSource(session.agentKind)}</span>` after the heading; the input `placeholder={session.agentTitle ?? session.sessionKey}`.
  - `SessionConversations.tsx`: a card "Conversations" listing `agentLabel(kind)`, title (or "Untitled"), the id in mono muted text, and `n operations`; rendered by `SessionPage` only when `conversations.length > 0`.
  - `OperationPage` "Agent call" card: Model fact shows `detail.agent.model ?? detail.agent.transcriptModel ?? "—"` with a muted `from transcript` when only the transcript value exists; same for Effort; add a "Conversation" fact with `conversationTitle` when present.
  - `OperationTable`: when `operation.agentModelSource === "transcript"`, append ` (transcript)` to the model line.
  - `SettingsPage`: in the Dashboard card add `<Toggle id="dashboard-agent-transcripts" label="Read agent transcripts" checked={draft.dashboard.agentTranscripts.enabled} onChange={value => setDashboard({ agentTranscripts: { enabled: value } })} />` and the description "Reads local Claude Code and Codex transcripts to show conversation titles and Claude's model and effort. Only titles, ids, model and effort are stored." Below it an `AgentTranscriptsStatus` block (in the same file, like `ModelPricesCard`): counts and last cycle time from `useTranscriptStatus()`, a "Clear matched data" button opening a confirmation `Dialog` ("Removes matched titles, models and conversations. Your session titles and notes are kept.") that calls `useClearTranscripts().mutate()`; show its error message inline.

- [ ] **Step 5: GREEN** — Step 3 command, then `npm run check` and `npm run build`.

- [ ] **Step 6: Commit** — `git commit -m "feat(dashboard-ui): agent titles, conversations, transcript model and settings"`.

---

### Task 8: Documentation, gates, plan row

**Files:**
- Modify: `README.md`, `plans/README.md`, `docs/superpowers/specs/2026-10-14-dashboard-agent-transcript-matching-design.md` (status line)

- [x] **Step 1: README.** Documented the setting, source paths, allowlisted fields and limits, per-cycle budget, exact matching, display/model precedence, clear behavior, retention, and all four dashboard-written derived tables. `Capabilities.cs` and `skills/sqlharness/SKILL.md` remain unchanged. `rg -n "three guarded writes|guarded writes" README.md src/SqlHarness.Dashboard` found the accurate four-write count in README and server security comments.
- [x] **Step 2: Gates** — Windows `just verify` passed at `64bbec5` (exit 0, `verify: OK`, stages=6). Linux verification was explicitly waived by the operator and was not run. The initial Windows gate at `5f25e19` exposed a nondeterministic exact-title assertion; the test was made scheduling-safe in `77348b4`, branch formatting was corrected in `64bbec5`, and the final gate passed. Final logs contain no case-sensitive `FAILED` match.
- [x] **Step 3: Plan row / spec status** — 070 marked DONE with its commit range and Windows result; the spec status is "implemented by Plan 070". The Linux waiver and unverified state are recorded in the evidence.
- [x] **Step 4: Commit** — README documentation: `5f25e19`; scheduling-safe regression assertion: `77348b4`; branch-scoped formatting corrections: `64bbec5`; plan/evidence status commit follows the verified implementation range.

Integration into `main` and any push only on a separate operator instruction.
