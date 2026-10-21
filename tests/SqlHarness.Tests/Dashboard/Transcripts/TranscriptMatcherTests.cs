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
    public void Version_labels_are_frozen_before_a_later_link_can_change_chronology()
    {
        using var f = new Fixture();
        var firstSession = JournalSeed.Session("mcp:first", "claude") with { Transport = JournalTransport.Mcp };
        var secondSession = JournalSeed.Session("mcp:second", "claude") with { Transport = JournalTransport.Mcp };
        var first = f.Seed.Compare(firstSession, "orders", "whole", "sha256:h0", "sha256:h1", agent: Call("toolu_first"));
        var second = f.Seed.Compare(secondSession, "orders", "whole", "sha256:h1", "sha256:h2", agent: Call("toolu_second"));
        var late = f.Seed.Compare(firstSession, "orders", "whole", "sha256:h2", "sha256:h3", agent: Call("toolu_late"));
        using (var connection = new SqliteConnection($"Data Source={f.Home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE operations SET started_at = CASE id WHEN $first THEN '2026-01-02T00:00:00.000Z' WHEN $second THEN '2026-01-03T00:00:00.000Z' WHEN $late THEN '2026-01-04T00:00:00.000Z' ELSE started_at END;";
            update.Parameters.AddWithValue("$first", first.OperationId);
            update.Parameters.AddWithValue("$second", second.OperationId);
            update.Parameters.AddWithValue("$late", late.OperationId);
            update.ExecuteNonQuery();
        }
        f.ClaudeFile("conversation", ClaudeFixtures.Assistant("conversation", "model", null,
            ("toolu_first", "mcp__s__sqlharness_compare"), ("toolu_second", "mcp__s__sqlharness_compare")));

        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);

        Assert.Equal(3, Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT * FROM compare_version_labels").Count);
        Assert.Equal(new[] { ("sha256:h0", "v0"), ("sha256:h1", "v1"), ("sha256:h2", "v2") },
            Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT sql_hash, label FROM compare_version_labels ORDER BY label")
                .Select(row => ((string)row["sql_hash"]!, (string)row["label"]!)).ToArray());

        f.ClaudeFile("conversation", ClaudeFixtures.Assistant("conversation", "model", null,
            ("toolu_late", "mcp__s__sqlharness_compare")));
        matcher.RunCycle(enabled: true);

        Assert.Equal(new[] { ("sha256:h0", "v0"), ("sha256:h1", "v1"), ("sha256:h2", "v2"), ("sha256:h3", "v3") },
            Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT sql_hash, label FROM compare_version_labels ORDER BY label")
                .Select(row => ((string)row["sql_hash"]!, (string)row["label"]!)).ToArray());
    }

    [Fact]
    public void Pending_earlier_child_blocks_only_the_unfrozen_label_suffix_until_exact_link_arrives()
    {
        using var f = new Fixture();
        var session = JournalSeed.Session("mcp:partial-freeze", "claude") with { Transport = JournalTransport.Mcp };
        var first = f.Seed.Compare(session, "orders", "whole", "sha256:h0", "sha256:h1");
        var pending = f.Seed.Operation(session, agent: Call("toolu_pending"));
        var second = f.Seed.Compare(session, "orders", "whole", "sha256:h1", "sha256:h2");
        JournalSeed.Link(f.Home.DatabasePath, first.OperationId, "claude", "conversation", null);
        JournalSeed.Link(f.Home.DatabasePath, second.OperationId, "claude", "conversation", null);
        using (var connection = new SqliteConnection($"Data Source={f.Home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE operations SET started_at = CASE id WHEN $first THEN '2026-01-01T00:00:00.000Z' WHEN $pending THEN '2026-01-02T00:00:00.000Z' WHEN $second THEN '2026-01-03T00:00:00.000Z' END;";
            update.Parameters.AddWithValue("$first", first.OperationId);
            update.Parameters.AddWithValue("$pending", pending.OperationId);
            update.Parameters.AddWithValue("$second", second.OperationId);
            update.ExecuteNonQuery();
        }

        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);
        Assert.Equal(new[] { ("sha256:h0", "v0"), ("sha256:h1", "v1") },
            Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT sql_hash, label FROM compare_version_labels ORDER BY label")
                .Select(row => ((string)row["sql_hash"]!, (string)row["label"]!)).ToArray());

        JournalSeed.Link(f.Home.DatabasePath, pending.OperationId, "claude", "conversation", null);
        matcher.RunCycle(enabled: true);
        Assert.Equal(new[] { ("sha256:h0", "v0"), ("sha256:h1", "v1"), ("sha256:h2", "v2") },
            Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT sql_hash, label FROM compare_version_labels ORDER BY label")
                .Select(row => ((string)row["sql_hash"]!, (string)row["label"]!)).ToArray());
    }

    [Fact]
    public void Pending_call_in_another_mcp_child_blocks_but_no_id_and_cli_rows_do_not()
    {
        using var f = new Fixture();
        var contextSession = JournalSeed.Session("mcp:context", "claude") with { Transport = JournalTransport.Mcp };
        var otherSession = JournalSeed.Session("mcp:other-child", "claude") with { Transport = JournalTransport.Mcp };
        var cliSession = JournalSeed.Session("cli:other", "claude");
        var noId = f.Seed.Operation(contextSession);
        var cliPending = f.Seed.Operation(cliSession, agent: Call("cli-call"));
        var mcpPending = f.Seed.Operation(otherSession, agent: Call("mcp-call"));
        var compare = f.Seed.Compare(contextSession, "orders", "whole", "sha256:h0", "sha256:h1");
        JournalSeed.Link(f.Home.DatabasePath, compare.OperationId, "claude", "conversation", null);
        using (var connection = new SqliteConnection($"Data Source={f.Home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE operations SET started_at = CASE id WHEN $noId THEN '2026-01-01T00:00:00.000Z' WHEN $cli THEN '2026-01-01T00:00:01.000Z' WHEN $pending THEN '2026-01-02T00:00:00.000Z' WHEN $compare THEN '2026-01-03T00:00:00.000Z' END;";
            update.Parameters.AddWithValue("$noId", noId.OperationId);
            update.Parameters.AddWithValue("$cli", cliPending.OperationId);
            update.Parameters.AddWithValue("$pending", mcpPending.OperationId);
            update.Parameters.AddWithValue("$compare", compare.OperationId);
            update.ExecuteNonQuery();
        }

        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);
        Assert.Empty(Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT * FROM compare_version_labels"));

        JournalSeed.Link(f.Home.DatabasePath, mcpPending.OperationId, "claude", "conversation", null);
        matcher.RunCycle(enabled: true);
        Assert.Equal(new[] { ("sha256:h0", "v0"), ("sha256:h1", "v1") },
            Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT sql_hash, label FROM compare_version_labels ORDER BY label")
                .Select(row => ((string)row["sql_hash"]!, (string)row["label"]!)).ToArray());
    }

    [Fact]
    public void Matcher_assigns_after_highest_persisted_label_when_older_hashes_are_no_longer_retained()
    {
        using var f = new Fixture();
        var session = JournalSeed.Session("mcp:retained-labels", "claude") with { Transport = JournalTransport.Mcp };
        var compare = f.Seed.Compare(session, "orders", "whole", "sha256:new0", "sha256:new1");
        JournalSeed.Link(f.Home.DatabasePath, compare.OperationId, "claude", "conversation", null);
        using (var connection = new SqliteConnection($"Data Source={f.Home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO compare_version_labels (context_kind, context_key, subject, scope, sql_hash, label) VALUES ('agent:claude', 'conversation', 'orders', 'whole', 'expired-hash-4', 'v4'), ('agent:claude', 'conversation', 'orders', 'whole', 'expired-hash-5', 'v5');";
            insert.ExecuteNonQuery();
        }

        f.Matcher().RunCycle(enabled: true);

        Assert.Equal(new[] { ("sha256:new0", "v6"), ("sha256:new1", "v7") },
            Journal.JournalDb.Rows(f.Home.DatabasePath, "SELECT sql_hash, label FROM compare_version_labels WHERE sql_hash LIKE 'sha256:%' ORDER BY label")
                .Select(row => ((string)row["sql_hash"]!, (string)row["label"]!)).ToArray());
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
    public async Task Codex_title_backfill_resumes_after_budget_and_targeted_lookup()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:old-codex", "codex"), agent: Call("exec-old", sessionId: "old-thread"));

        var indexPath = f.Homes.CodexSessionIndex;
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        using (var writer = new StreamWriter(indexPath))
        {
            writer.WriteLine("{\"id\":\"target-thread\",\"thread_name\":\"Target title\",\"updated_at\":\"t\"}");
            var padding = new string('x', 1024 * 1024);
            for (var i = 0; i < 33; i++)
                writer.WriteLine(padding);
            writer.WriteLine("{\"id\":\"old-thread\",\"thread_name\":\"Older title\",\"updated_at\":\"t2\"}");
        }

        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);
        Assert.Equal(1, matcher.CodexTitleBackfillIdentityCount);
        var old = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "old-thread");
        Assert.Null(old["title"]);

        f.Seed.Operation(JournalSeed.Session("mcp:new-codex", "codex"), agent: Call("exec-target", sessionId: "target-thread"));
        var targetOperationId = Convert.ToInt64(Journal.JournalDb.Rows(f.Home.DatabasePath,
            "SELECT id FROM operations WHERE agent_session_id = 'target-thread'").Single()["id"]);
        await matcher.RunTargetedAsync([targetOperationId], CancellationToken.None);
        Assert.Equal(2, matcher.CodexTitleBackfillIdentityCount);

        var target = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "target-thread");
        Assert.Equal("Target title", target["title"]);
        Assert.Equal(1, matcher.LastCodexLookupIdentityCount);

        for (var i = 0; i < 3; i++)
        {
            matcher.RunCycle(enabled: true);
            old = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "old-thread");
            if ((string?)old["title"] == "Older title")
                break;
        }

        Assert.Equal("Older title", old["title"]);
        Assert.Equal(0, matcher.CodexTitleBackfillIdentityCount);
    }

    [Fact]
    public void Full_sweep_evicts_orphan_null_title_state_and_allows_reintroduction()
    {
        using var f = new Fixture();
        var operation = f.Seed.Operation(JournalSeed.Session("mcp:reused", "codex"), agent: Call("exec-reused", sessionId: "reused"));
        f.CodexIndex();
        var matcher = f.Matcher();

        matcher.RunCycle(enabled: true);
        Assert.Equal(1, matcher.CodexTitleBackfillIdentityCount);

        using (var connection = new SqliteConnection($"Data Source={f.Home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM operations WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", operation.OperationId);
            delete.ExecuteNonQuery();
        }

        matcher.RunCycle(enabled: true);
        Assert.Equal(0, matcher.CodexTitleBackfillIdentityCount);

        f.Seed.Operation(JournalSeed.Session("mcp:reused-again", "codex"), agent: Call("exec-reused-again", sessionId: "reused"));
        f.CodexIndex("""{"id":"reused","thread_name":"Reintroduced title","updated_at":"t"}""");
        matcher.RunCycle(enabled: true);

        Assert.Equal("Reintroduced title", Assert.Single(f.Links())["title"]);
        Assert.Equal(0, matcher.CodexTitleBackfillIdentityCount);
    }

    [Fact]
    public void Full_sweep_prunes_kind_qualified_identity_independently()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:codex", "codex"), agent: Call("exec-codex", sessionId: "shared-id"));
        var opencode = f.Seed.Operation(JournalSeed.Session("mcp:opencode", "opencode"), agent: Call("exec-opencode", sessionId: "shared-id"));
        f.CodexIndex();
        var matcher = f.Matcher();

        matcher.RunCycle(enabled: true);
        Assert.Equal(2, matcher.CodexTitleBackfillIdentityCount);

        using (var connection = new SqliteConnection($"Data Source={f.Home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM operations WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", opencode.OperationId);
            delete.ExecuteNonQuery();
        }

        matcher.RunCycle(enabled: true);

        Assert.Equal(1, matcher.CodexTitleBackfillIdentityCount);
        Assert.Contains(f.Links(), row => (string)row["agent_kind"]! == "codex" && (string)row["conversation_id"]! == "shared-id");
    }

    [Fact]
    public void In_flight_batch_identity_survives_until_the_next_full_sweep()
    {
        using var f = new Fixture();
        var codex = f.Seed.Operation(JournalSeed.Session("mcp:codex-inflight", "codex"), agent: Call("exec-inflight", sessionId: "inflight"));
        f.Seed.Operation(JournalSeed.Session("mcp:claude-trigger", "claude"), agent: Call("toolu-trigger"));
        f.CodexIndex();
        var deleted = false;
        var matcher = new TranscriptMatcher(f.Home.DatabasePath, f.Homes, new Journal.FixedTimeProvider(Now))
        {
            DirectoryExists = _ =>
            {
                if (!deleted)
                {
                    using var connection = new SqliteConnection($"Data Source={f.Home.DatabasePath};Pooling=False");
                    connection.Open();
                    using var delete = connection.CreateCommand();
                    delete.CommandText = "DELETE FROM operations WHERE id = $id;";
                    delete.Parameters.AddWithValue("$id", codex.OperationId);
                    delete.ExecuteNonQuery();
                    deleted = true;
                }
                return false;
            },
        };

        matcher.RunCycle(enabled: true);

        Assert.True(deleted);
        Assert.Equal(1, matcher.CodexTitleBackfillIdentityCount);
        Assert.Single(Journal.JournalDb.Rows(f.Home.DatabasePath,
            "SELECT 1 FROM agent_conversations WHERE agent_kind = 'codex' AND conversation_id = 'inflight' AND title IS NULL"));

        matcher.RunCycle(enabled: true);

        Assert.Equal(0, matcher.CodexTitleBackfillIdentityCount);
    }

    [Fact]
    public void Repeated_retention_cycles_bound_backfill_state_to_current_operations()
    {
        using var f = new Fixture();
        f.CodexIndex();
        var matcher = f.Matcher();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var operations = Enumerable.Range(0, 20)
                .Select(i => f.Seed.Operation(JournalSeed.Session($"mcp:cycle-{cycle}-{i}", "codex"),
                    agent: Call($"exec-{cycle}-{i}", sessionId: $"thread-{cycle}-{i}")))
                .ToArray();

            matcher.RunCycle(enabled: true);
            Assert.Equal(20, matcher.CodexTitleBackfillIdentityCount);

            using (var connection = new SqliteConnection($"Data Source={f.Home.DatabasePath};Pooling=False"))
            {
                connection.Open();
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM operations WHERE id IN (" + string.Join(",", operations.Select((_, i) => "$id" + i)) + ");";
                for (var i = 0; i < operations.Length; i++)
                    delete.Parameters.AddWithValue("$id" + i, operations[i].OperationId);
                delete.ExecuteNonQuery();
            }

            matcher.RunCycle(enabled: true);
            Assert.Equal(0, matcher.CodexTitleBackfillIdentityCount);
        }
    }

    [Fact]
    public async Task A_targeted_codex_backfill_keeps_other_unresolved_titles_in_scope()
    {
        using var f = new Fixture();
        f.Seed.Operation(JournalSeed.Session("mcp:claude-budget", "claude"), agent: Call("toolu_budget"));
        f.Seed.Operation(JournalSeed.Session("mcp:old-codex", "codex"), agent: Call("exec-old", sessionId: "old-thread"));
        var paddingPath = Path.Combine(f.Homes.ClaudeProjectDirectory("/work"), "budget.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(paddingPath)!);
        File.WriteAllText(paddingPath, new string('x', (int)TranscriptMatcher.CycleBudgetBytes));
        f.CodexIndex("""{"id":"old-thread","thread_name":"Older title","updated_at":"t"}""");

        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);
        var old = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "old-thread");
        Assert.Null(old["title"]);

        f.Seed.Operation(JournalSeed.Session("mcp:new-codex", "codex"), agent: Call("exec-new", sessionId: "new-thread"));
        f.CodexIndex("""{"id":"new-thread","thread_name":"New title","updated_at":"t2"}""");
        var newOperationId = Convert.ToInt64(Journal.JournalDb.Rows(f.Home.DatabasePath,
            "SELECT id FROM operations WHERE agent_session_id = 'new-thread'").Single()["id"]);
        await matcher.RunTargetedAsync([newOperationId], CancellationToken.None);

        old = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "old-thread");
        Assert.Null(old["title"]);
        Assert.Equal(1, matcher.LastCodexLookupIdentityCount);
        var current = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "new-thread");
        Assert.Equal("New title", current["title"]);

        File.Delete(paddingPath);
        matcher.RunCycle(enabled: true);

        old = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "old-thread");
        Assert.Equal("Older title", old["title"]);
    }

    [Fact]
    public async Task Targeted_codex_cycle_does_not_expand_lookup_to_other_attempted_titles()
    {
        using var f = new Fixture();
        for (var i = 0; i < 24; i++)
            f.Seed.Operation(JournalSeed.Session($"mcp:old-{i}", "codex"), agent: Call($"exec-old-{i}", sessionId: $"old-thread-{i}"));
        f.CodexIndex("""{"id":"unrelated-index-entry","thread_name":"Unrelated","updated_at":"t"}""");

        var matcher = f.Matcher();
        matcher.RunCycle(enabled: true);

        f.Seed.Operation(JournalSeed.Session("mcp:target", "codex"), agent: Call("exec-target", sessionId: "target-thread"));
        f.CodexIndex("""{"id":"target-thread","thread_name":"Target title","updated_at":"t2"}""");
        var targetOperationId = Convert.ToInt64(Journal.JournalDb.Rows(f.Home.DatabasePath,
            "SELECT id FROM operations WHERE agent_session_id = 'target-thread'").Single()["id"]);

        await matcher.RunTargetedAsync([targetOperationId], CancellationToken.None);

        Assert.Equal(1, matcher.LastCodexLookupIdentityCount);
        var link = Assert.Single(f.Links(), row => (string)row["conversation_id"]! == "target-thread");
        Assert.Equal("Target title", link["title"]);
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