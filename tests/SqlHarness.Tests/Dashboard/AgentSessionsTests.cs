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

        using var filteredResponse = await client.GetAsync("/api/agent-sessions?archived=any&agent=codex&q=Conversation%20title&hasCompare=false");
        using var filtered = JsonDocument.Parse(await filteredResponse.Content.ReadAsStringAsync());
        Assert.Single(filtered.RootElement.GetProperty("items").EnumerateArray());
        using var noMatchResponse = await client.GetAsync("/api/agent-sessions?archived=any&q=missing");
        using var noMatch = JsonDocument.Parse(await noMatchResponse.Content.ReadAsStringAsync());
        Assert.Empty(noMatch.RootElement.GetProperty("items").EnumerateArray());

        using var patch = await client.SendAsync(Patch("/api/agent-sessions/codex/thread-1/annotation", "{\"title\":\"Owner title\",\"note\":\"A private note\"}"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var after = JsonDocument.Parse(await client.GetStringAsync("/api/agent-sessions?archived=any"));
        Assert.Equal("Owner title", Assert.Single(after.RootElement.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        using var detail = JsonDocument.Parse(await client.GetStringAsync("/api/agent-sessions/codex/thread-1"));
        Assert.Equal("A private note", detail.RootElement.GetProperty("note").GetString());
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
    public async Task Agent_session_search_matches_child_client_title()
    {
        using var home = new TempHome();
        var session = JournalSeed.Session("claude:client-title", "claude") with
        {
            Transport = JournalTransport.Mcp,
            ClientTitle = "Only this child has the client title",
        };
        var operation = new JournalSeed(home.DatabasePath).Operation(session);
        JournalSeed.Link(home.DatabasePath, operation.OperationId, "claude", "conversation-client-title", null);
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync("/api/agent-sessions?archived=any&q=Only%20this%20child%20has%20the%20client%20title");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Unmatched_sessions_exclude_children_from_agent_rows_beyond_the_first_agent_page()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var targetSession = JournalSeed.Session("claude:older-child", "claude") with { Transport = JournalTransport.Mcp };
        var targetOperation = seed.Operation(targetSession);
        JournalSeed.Link(home.DatabasePath, targetOperation.OperationId, "claude", "older-conversation", null);
        seed.Operation(JournalSeed.Session("claude:truly-unmatched", "claude"));
        for (var i = 0; i < 50; i++)
            seed.Operation(JournalSeed.Session($"codex:later-{i:D2}", "codex"), agent: new AgentCallMetadata(null, null, null, $"later-conversation-{i:D2}", null, null, null));

        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;
        using var agentPage = JsonDocument.Parse(await client.GetStringAsync("/api/agent-sessions?archived=false&starred=false&limit=50"));
        Assert.DoesNotContain(agentPage.RootElement.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("key").GetProperty("conversationId").GetString() == "older-conversation");

        using var response = await client.GetAsync("/api/agent-sessions/unmatched?archived=false&starred=false&limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var unmatched = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(unmatched, item => item.GetProperty("sessionKey").GetString() == "claude:truly-unmatched");
        Assert.DoesNotContain(unmatched, item => item.GetProperty("sessionKey").GetString() == "claude:older-child");
    }

    [Fact]
    public async Task Agent_session_detail_and_annotation_resolve_keys_after_the_first_list_page()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        for (var i = 0; i < 51; i++)
            seed.Operation(JournalSeed.Session($"codex:child-{i:D2}", "codex"),
                agent: new AgentCallMetadata(null, null, null, $"thread-{i:D2}", null, null, null));
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        using var pageResponse = await client.GetAsync("/api/agent-sessions?archived=any&limit=50");
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
        var visibleIds = page.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("key").GetProperty("conversationId").GetString()).ToHashSet();
        var target = Enumerable.Range(0, 51).Select(i => $"thread-{i:D2}").Single(id => !visibleIds.Contains(id));

        using var detail = await client.GetAsync($"/api/agent-sessions/codex/{target}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(target, detailJson.RootElement.GetProperty("agentSession").GetProperty("key").GetProperty("conversationId").GetString());

        using var patch = await client.SendAsync(Patch($"/api/agent-sessions/codex/{target}/annotation", "{\"title\":\"Beyond page\"}"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var patchJson = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        Assert.Equal("Beyond page", patchJson.RootElement.GetProperty("title").GetString());
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
    public async Task Agent_session_detail_keeps_members_beyond_child_limit_and_uses_member_recency()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var shared = JournalSeed.Session("claude:shared-limit", "claude") with { Transport = JournalTransport.Mcp };
        var olderConversationOperation = seed.Operation(shared, agent: new AgentCallMetadata("tool-a", null, null, null, null, null, null));
        JournalSeed.Link(home.DatabasePath, olderConversationOperation.OperationId, "claude", "conversation-a", "A");

        for (var i = 0; i < 500; i++)
        {
            var otherConversationOperation = seed.Operation(shared, agent: new AgentCallMetadata($"tool-b-{i}", null, null, null, null, null, null));
            JournalSeed.Link(home.DatabasePath, otherConversationOperation.OperationId, "claude", "conversation-b", "B");
        }

        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync("/api/agent-sessions/claude/conversation-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = detail.RootElement.GetProperty("agentSession");
        Assert.Equal(1, session.GetProperty("exact").GetInt32());
        var operation = Assert.Single(detail.RootElement.GetProperty("operations").EnumerateArray());
        Assert.Equal(olderConversationOperation.OperationId, operation.GetProperty("operation").GetProperty("id").GetInt64());
        Assert.Equal("exact", operation.GetProperty("membership").GetString());
        Assert.Equal("2026-10-07T09:00:01.000Z", session.GetProperty("lastSeen").GetString());
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
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/agent-sessions/claude/conversation-1/optimization")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/agent-sessions/claude/conversation-1/optimization/compare")).StatusCode);
    }

    [Fact]
    public async Task Agent_optimization_is_provisional_while_an_earlier_exact_call_is_unmatched_and_get_does_not_freeze_labels()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var child = JournalSeed.Session("claude:optimization", "claude") with { Transport = JournalTransport.Mcp };
        seed.Operation(child, agent: new AgentCallMetadata("tool-pending", null, null, null, null, null, null));
        var compare = seed.Compare(child, "orders", "whole", "sha256:h0", "sha256:h1",
            agent: new AgentCallMetadata("tool-linked", null, null, null, null, null, null));
        JournalSeed.Link(home.DatabasePath, compare.OperationId, "claude", "conversation-1", "Conversation");
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;
        var before = SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath, "SELECT * FROM compare_version_labels").Count;

        using var summaryResponse = await client.GetAsync("/api/agent-sessions/claude/conversation-1/optimization");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
        Assert.True(summary.RootElement.GetProperty("versionLabelsProvisional").GetBoolean());
        Assert.Equal("versions may renumber while matching catches up", summary.RootElement.GetProperty("versionLabelNote").GetString());

        using var compareResponse = await client.GetAsync("/api/agent-sessions/claude/conversation-1/optimization/compare");
        Assert.Equal(HttpStatusCode.OK, compareResponse.StatusCode);
        using var comparison = JsonDocument.Parse(await compareResponse.Content.ReadAsStringAsync());
        Assert.True(comparison.RootElement.GetProperty("versionLabelsProvisional").GetBoolean());
        Assert.Equal("versions may renumber while matching catches up", comparison.RootElement.GetProperty("versionLabelNote").GetString());
        Assert.Equal(before, SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath, "SELECT * FROM compare_version_labels").Count);
    }

    [Fact]
    public async Task Agent_optimization_joins_exact_mcp_histories_and_leaves_cli_history_on_its_child()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var firstSession = JournalSeed.Session("codex:first", "codex") with { Transport = JournalTransport.Mcp };
        var secondSession = JournalSeed.Session("codex:second", "codex") with { Transport = JournalTransport.Mcp };
        var cliSession = JournalSeed.Session("codex:cli", "codex");
        var first = seed.Compare(firstSession, "orders", "whole", "sha256:h0", "sha256:h1",
            agent: new AgentCallMetadata(null, null, null, "thread-1", null, null, null));
        seed.Compare(secondSession, "orders", "whole", "sha256:h1", "sha256:h2",
            agent: new AgentCallMetadata(null, null, null, "thread-1", null, null, null));
        seed.Compare(cliSession, "orders", "whole", "sha256:h2", "sha256:h3",
            agent: new AgentCallMetadata(null, null, null, "thread-1", null, null, null));
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync("/api/agent-sessions/codex/thread-1/optimization");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var aggregate = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var aggregateHashes = aggregate.RootElement.GetProperty("subjects").EnumerateArray()
            .SelectMany(subject => subject.GetProperty("groups").EnumerateArray())
            .SelectMany(group => group.GetProperty("scopes").EnumerateArray())
            .SelectMany(scope => scope.GetProperty("versions").EnumerateArray())
            .Select(version => version.GetProperty("hash").GetString()).ToArray();
        Assert.Equal(new[] { "sha256:h0", "sha256:h1", "sha256:h2" }, aggregateHashes);

        var childId = (long)SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath,
            "SELECT id FROM sessions WHERE session_key = 'codex:cli'").Single()["id"]!;
        using var childResponse = await client.GetAsync($"/api/sessions/{childId}/optimization");
        Assert.Equal(HttpStatusCode.OK, childResponse.StatusCode);
        using var child = JsonDocument.Parse(await childResponse.Content.ReadAsStringAsync());
        Assert.Contains(child.RootElement.GetProperty("subjects").EnumerateArray()
            .SelectMany(subject => subject.GetProperty("groups").EnumerateArray())
            .SelectMany(group => group.GetProperty("scopes").EnumerateArray())
            .SelectMany(scope => scope.GetProperty("versions").EnumerateArray())
            .Select(version => version.GetProperty("hash").GetString()), hash => hash == "sha256:h3");
        Assert.NotEqual(0, first.OperationId);
    }

    [Fact]
    public async Task Agent_optimization_excludes_inferred_compare_kept_in_child_history()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("claude:inferred", "claude") with { Transport = JournalTransport.Mcp };
        var first = seed.Compare(session, "orders", "whole", "sha256:h0", "sha256:h1",
            agent: new AgentCallMetadata("tool-first", null, null, null, null, null, null));
        var inferred = seed.Compare(session, "orders", "whole", "sha256:h1", "sha256:h3");
        var last = seed.Compare(session, "orders", "whole", "sha256:h1", "sha256:h2",
            agent: new AgentCallMetadata("tool-last", null, null, null, null, null, null));
        JournalSeed.Link(home.DatabasePath, first.OperationId, "claude", "conversation-inferred", null);
        JournalSeed.Link(home.DatabasePath, last.OperationId, "claude", "conversation-inferred", null);
        await using var dashboard = await StartEnabled(home);
        var (_, client) = dashboard;

        using var aggregateResponse = await client.GetAsync("/api/agent-sessions/claude/conversation-inferred/optimization");
        Assert.Equal(HttpStatusCode.OK, aggregateResponse.StatusCode);
        using var aggregate = JsonDocument.Parse(await aggregateResponse.Content.ReadAsStringAsync());
        var aggregateHashes = aggregate.RootElement.GetProperty("subjects").EnumerateArray()
            .SelectMany(subject => subject.GetProperty("groups").EnumerateArray())
            .SelectMany(group => group.GetProperty("scopes").EnumerateArray())
            .SelectMany(scope => scope.GetProperty("versions").EnumerateArray())
            .Select(version => version.GetProperty("hash").GetString()).ToArray();
        Assert.DoesNotContain("sha256:h3", aggregateHashes);

        using var detailResponse = await client.GetAsync("/api/agent-sessions/claude/conversation-inferred");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Contains(detail.RootElement.GetProperty("operations").EnumerateArray(), operation =>
            operation.GetProperty("operation").GetProperty("id").GetInt64() == inferred.OperationId
            && operation.GetProperty("membership").GetString() == "inferred");

        var childId = (long)SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath,
            "SELECT id FROM sessions WHERE session_key = 'claude:inferred'").Single()["id"]!;
        using var childResponse = await client.GetAsync($"/api/sessions/{childId}/optimization");
        Assert.Equal(HttpStatusCode.OK, childResponse.StatusCode);
        using var child = JsonDocument.Parse(await childResponse.Content.ReadAsStringAsync());
        Assert.Contains(child.RootElement.GetProperty("subjects").EnumerateArray()
            .SelectMany(subject => subject.GetProperty("groups").EnumerateArray())
            .SelectMany(group => group.GetProperty("scopes").EnumerateArray())
            .SelectMany(scope => scope.GetProperty("versions").EnumerateArray())
            .Select(version => version.GetProperty("hash").GetString()), hash => hash == "sha256:h3");
        Assert.NotEqual(inferred.OperationId, last.OperationId);
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