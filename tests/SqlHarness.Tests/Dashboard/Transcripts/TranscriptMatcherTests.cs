using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Dashboard.Transcripts;
using SqlHarness.Tests.Dashboard;

namespace SqlHarness.Tests.Dashboard.Transcripts;

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

    private static long DataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return (long)command.ExecuteScalar()!;
    }

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
    public void Shared_observer_connection_ignores_matcher_commits_but_sees_external_commits()
    {
        using var f = new Fixture();
        var session = JournalSeed.Session("mcp:shared-observer", "claude");
        f.Seed.Operation(session, agent: Call("toolu_shared"));
        f.ClaudeFile("conv-shared", ClaudeFixtures.Assistant("conv-shared", "model", null, ("toolu_shared", "mcp__s__sqlharness_query")));
        var matcher = f.Matcher();
        using var connection = matcher.OpenJournalConnection()!;
        var before = DataVersion(connection);

        matcher.RunCycle(enabled: true, journalConnection: connection);

        Assert.Equal(before, DataVersion(connection));
        Assert.Single(f.Links());

        f.Seed.Operation(session, agent: Call("toolu_external"));

        Assert.NotEqual(before, DataVersion(connection));
    }

    [Fact]
    public void A_tool_use_read_before_the_operation_exists_links_on_a_later_cycle()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_0"));
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
    public void A_staged_claude_call_is_not_pruned_by_a_codex_operation_with_the_same_id()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("other-tool"));
        f.Seed.Operation(JournalSeed.Session("mcp:x", "codex"), agent: Call("shared-id", sessionId: "codex-session"));
        f.ClaudeFile("conv-1", ClaudeFixtures.Assistant("conv-1", "m", null, ("shared-id", "mcp__s__sqlharness_query")));
        f.CodexIndex("""{"id":"codex-session","thread_name":"Codex","updated_at":"t"}""");
        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);

        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("shared-id"));
        matcher.RunCycle(enabled: true);

        Assert.Equal(["codex", "claude"], f.Links().Select(r => (string)r["agent_kind"]!));
    }

    [Fact]
    public void Reads_more_than_one_cycle_budget_across_multiple_cycles()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_after_budget"));
        var path = Path.Combine(f.Homes.ClaudeProjectDirectory("/work"), "large.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new StreamWriter(path))
        {
            var padding = new string('x', 1024 * 1024);
            for (var i = 0; i < 33; i++)
                writer.WriteLine(padding);
            writer.WriteLine(ClaudeFixtures.Assistant("large", "m", null, ("toolu_after_budget", "mcp__s__sqlharness_query")));
        }

        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);
        Assert.Empty(f.Links());
        matcher.RunCycle(enabled: true);

        Assert.Single(f.Links());
    }

    [Fact]
    public async Task Clear_waits_for_an_inflight_cycle_before_removing_its_rows()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:c", "claude"), agent: Call("toolu_1"));
        f.ClaudeFile("conv-1", ClaudeFixtures.Assistant("conv-1", "m", null, ("toolu_1", "mcp__s__sqlharness_query")));
        using var scanning = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var matcher = new TranscriptMatcher(f.Home.DatabasePath, f.Homes, new Journal.FixedTimeProvider(Now))
        {
            DirectoryExists = path =>
            {
                scanning.Set();
                resume.Wait();
                return Directory.Exists(path);
            },
        };
        var cycle = Task.Run(() => matcher.RunCycle(enabled: true));
        Assert.True(scanning.Wait(TimeSpan.FromSeconds(5)));
        var clearAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clear = Task.Run(() =>
        {
            clearAttempted.SetResult();
            return matcher.Clear();
        });
        await clearAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.False(clear.IsCompleted);

        resume.Set();
        await cycle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await clear.WaitAsync(TimeSpan.FromSeconds(5)));
        foreach (var table in new[] { "operation_agent_links", "agent_tool_calls", "agent_conversations", "transcript_cursors" })
            Assert.Empty(Journal.JournalDb.Rows(f.Home.DatabasePath, $"SELECT 1 FROM {table}"));
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
    public void Codex_links_without_index_timing()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:x", "codex"), agent: Call("exec-known", sessionId: "known"));
        f.CodexIndex(
            """{"id":"late-operation","thread_name":"Already indexed","updated_at":"t"}""",
            """{"id":"known","thread_name":"Known thread","updated_at":"t"}""");

        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);
        Assert.Contains(f.Links(), row => (string)row["conversation_id"]! == "known");

        f.Seed.Operation(JournalSeed.Session("mcp:open", "opencode"), agent: Call("exec-open", sessionId: "open-operation"));
        matcher.RunCycle(enabled: true);
        var open = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "open-operation");
        Assert.Equal("opencode", open["agent_kind"]);

        f.Seed.Operation(JournalSeed.Session("mcp:y", "codex"), agent: Call("exec-late", sessionId: "late-operation"));
        matcher.RunCycle(enabled: true);

        var late = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "late-operation");
        Assert.Equal("Already indexed", late["title"]);
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