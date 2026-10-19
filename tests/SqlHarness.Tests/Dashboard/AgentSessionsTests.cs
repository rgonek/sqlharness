using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class AgentSessionsTests
{
    private static async Task<DashboardServerTests.AuthenticatedDashboard> StartEnabled(TempHome home)
    {
        File.WriteAllText(Path.Combine(home.Path, "config.json"), "{\"dashboard\":{\"agentTranscripts\":{\"enabled\":true}}}");
        return await DashboardServerTests.StartAuthenticated(home);
    }

    private static HttpRequestMessage Patch(string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(DashboardSecurity.WriteHeader, "1");
        return request;
    }

    [Fact]
    public async Task Agent_session_list_includes_children_counts_and_title_precedence()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var call = new AgentCallMetadata(null, null, null, "thread-1", null, null, null);
        seed.Operation(JournalSeed.Session("codex:child-1", "codex"), agent: call);
        seed.Operation(JournalSeed.Session("codex:child-2", "codex"), agent: call);
        JournalSeed.Annotate(home.DatabasePath, "codex:child-2", "Newest child title");
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        using var conversation = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False");
        conversation.Open();
        using (var insert = conversation.CreateCommand())
        {
            insert.CommandText = "INSERT INTO agent_conversations (agent_kind, conversation_id, title, updated_at) VALUES ('codex', 'thread-1', 'Conversation title', '2026-10-07T09:00:00.000Z');";
            insert.ExecuteNonQuery();
        }

        using var response = await client.GetAsync("/api/agent-sessions?archived=any");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Conversation title", session.GetProperty("title").GetString());
        Assert.Equal(2, session.GetProperty("exact").GetInt32());
        Assert.Equal(0, session.GetProperty("inferred").GetInt32());
        Assert.Equal(0, session.GetProperty("unassigned").GetInt32());
        Assert.Equal(2, session.GetProperty("children").GetArrayLength());

        using var patch = await client.SendAsync(Patch("/api/agent-sessions/codex/thread-1/annotation", "{\"title\":\"Owner title\"}"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var after = JsonDocument.Parse(await client.GetStringAsync("/api/agent-sessions?archived=any"));
        Assert.Equal("Owner title", Assert.Single(after.RootElement.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Agent_session_title_falls_back_to_the_newest_child_annotation()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("codex:child", "codex"),
            agent: new AgentCallMetadata(null, null, null, "thread-1", null, null, null));
        JournalSeed.Annotate(home.DatabasePath, "codex:child", "Child title");
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        using var response = JsonDocument.Parse(await client.GetStringAsync("/api/agent-sessions"));
        Assert.Equal("Child title", Assert.Single(response.RootElement.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Agent_session_patch_reuses_session_annotation_validation_and_matching_flag_is_live()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("codex:child", "codex"),
            agent: new AgentCallMetadata(null, null, null, "thread-1", null, null, "subagent"));
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        using var sessions = JsonDocument.Parse(await client.GetStringAsync("/api/sessions"));
        Assert.True(sessions.RootElement.GetProperty("matching").GetBoolean());
        using var unguarded = new HttpRequestMessage(HttpMethod.Patch, "/api/agent-sessions/codex/thread-1/annotation")
        {
            Content = new StringContent("{\"title\":\"blocked\"}", Encoding.UTF8, "application/json"),
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(unguarded)).StatusCode);
        using var invalid = await client.SendAsync(Patch("/api/agent-sessions/codex/thread-1/annotation", "{\"title\":42}"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var detail = JsonDocument.Parse(await client.GetStringAsync("/api/agent-sessions/codex/thread-1"));
        Assert.Equal("subagent of unknown parent", detail.RootElement.GetProperty("agentSession").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Shared_child_unassigned_operations_are_duplicated_per_agent_detail_and_counted_per_row()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("claude:shared", "claude");
        var first = seed.Operation(session, agent: new AgentCallMetadata("tool-a", null, null, null, null, null, null));
        var second = seed.Operation(session, agent: new AgentCallMetadata("tool-b", null, null, null, null, null, null));
        var unlinked = seed.Operation(session, agent: new AgentCallMetadata("tool-c", null, null, null, null, null, null));
        JournalSeed.Link(home.DatabasePath, first.OperationId, "claude", "conversation-a", "A");
        JournalSeed.Link(home.DatabasePath, second.OperationId, "claude", "conversation-b", "B");
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        foreach (var conversationId in new[] { "conversation-a", "conversation-b" })
        {
            using var response = await client.GetAsync($"/api/agent-sessions/claude/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var operations = detail.RootElement.GetProperty("operations").EnumerateArray().ToArray();
            Assert.Contains(operations, item => item.GetProperty("operation").GetProperty("id").GetInt64() == unlinked.OperationId
                && item.GetProperty("membership").GetString() == "unassigned");
            Assert.Single(operations, item => item.GetProperty("membership").GetString() == "exact");
        }

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/agent-sessions?archived=any"));
        Assert.Equal(2, list.RootElement.GetProperty("items").GetArrayLength());
        Assert.All(list.RootElement.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(1, item.GetProperty("exact").GetInt32());
            Assert.Equal(1, item.GetProperty("unassigned").GetInt32());
            Assert.Equal(1, Assert.Single(item.GetProperty("children").EnumerateArray()).GetProperty("unassigned").GetInt32());
        });
    }

    [Fact]
    public async Task Agent_session_aggregate_endpoints_are_hidden_when_matching_is_disabled()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        using var sessions = JsonDocument.Parse(await client.GetStringAsync("/api/sessions"));
        Assert.False(sessions.RootElement.GetProperty("matching").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/agent-sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/agent-sessions/codex/thread-1")).StatusCode);
        using var patch = Patch("/api/agent-sessions/codex/thread-1/annotation", "{\"title\":\"hidden\"}");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(patch)).StatusCode);
    }

    [Fact]
    public async Task Clear_keeps_agent_annotations_and_retention_removes_them_with_the_final_anchored_operation()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("claude:child", "claude");
        var first = seed.Operation(session, agent: new AgentCallMetadata("tool-1", null, null, null, null, null, null));
        var last = seed.Operation(session, agent: new AgentCallMetadata("tool-2", null, null, null, null, null, null));
        JournalSeed.Link(home.DatabasePath, first.OperationId, "claude", "conversation-1", "Conversation");
        JournalSeed.Link(home.DatabasePath, last.OperationId, "claude", "conversation-1", "Conversation");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE operations SET started_at = CASE id WHEN $first THEN '2020-01-01T00:00:00.000Z' ELSE '2026-10-09T23:00:00.000Z' END WHERE id IN ($first, $last);";
            update.Parameters.AddWithValue("$first", first.OperationId);
            update.Parameters.AddWithValue("$last", last.OperationId);
            update.ExecuteNonQuery();
        }
        var now = new SqlHarness.Tests.Journal.FixedTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;
        using var annotation = await client.SendAsync(Patch("/api/agent-sessions/claude/conversation-1/annotation", "{\"title\":\"Keep this\"}"));
        Assert.Equal(HttpStatusCode.OK, annotation.StatusCode);

        using var clear = new HttpRequestMessage(HttpMethod.Post, "/api/transcripts/clear") { Content = JsonContent.Create(new { }) };
        clear.Headers.Add(DashboardSecurity.WriteHeader, "1");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(clear)).StatusCode);
        Assert.Equal("Keep this", SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath,
            "SELECT title FROM agent_session_annotations").Single()["title"]);

        JournalRetention.Run(home.DatabasePath, new JournalConfig
        {
            Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 1, MaxSizeMb = 500 },
        }, new FakeProcesses(), now);
        Assert.Equal("Keep this", SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath,
            "SELECT title FROM agent_session_annotations").Single()["title"]);

        now.Now = now.Now.AddDays(2);
        JournalRetention.Run(home.DatabasePath, new JournalConfig
        {
            Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 1, MaxSizeMb = 500 },
        }, new FakeProcesses(), now);
        Assert.Empty(SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath, "SELECT * FROM agent_session_annotations"));
    }
}