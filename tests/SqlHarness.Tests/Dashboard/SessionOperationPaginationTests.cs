using System.Net;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace SqlHarness.Tests.Dashboard;

public sealed class SessionOperationPaginationTests
{
    [Fact]
    public async Task Ordinary_session_operations_are_read_in_bounded_pages_from_the_journal()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var ordinary = JournalSeed.Session("cli:ordinary-pagination");
        seed.Operation(ordinary);
        var foreign = JournalSeed.Session("cli:foreign-pagination");
        seed.Operation(foreign);
        var sessions = new SqlHarness.Dashboard.JournalReader(home.DatabasePath, new FakeProcesses())
            .Sessions(new SqlHarness.Dashboard.SessionQuery()).Items;
        var ordinaryId = sessions.Single(item => item.SessionKey == ordinary.SessionKey).Id;
        var foreignId = sessions.Single(item => item.SessionKey == foreign.SessionKey).Id;
        InsertOperations(home.DatabasePath, ordinaryId, 1000);
        InsertOperations(home.DatabasePath, foreignId, 1000);
        var empty = JournalSeed.Session("cli:empty-pagination");
        seed.Operation(empty);
        var emptyId = new SqlHarness.Dashboard.JournalReader(home.DatabasePath, new FakeProcesses())
            .Sessions(new SqlHarness.Dashboard.SessionQuery()).Items.Single(item => item.SessionKey == empty.SessionKey).Id;
        using (var emptyConnection = new SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            emptyConnection.Open();
            using var delete = emptyConnection.CreateCommand();
            delete.CommandText = "DELETE FROM operations WHERE session_id = $session;";
            delete.Parameters.AddWithValue("$session", emptyId);
            delete.ExecuteNonQuery();
        }

        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;
        var collected = new List<long>();
        long? cursor = null;
        int[] pageSizes = [500, 500, 1];
        for (var pageIndex = 0; pageIndex < pageSizes.Length; pageIndex++)
        {
            var url = $"/api/sessions/{ordinaryId}/operations?limit=500" + (cursor is null ? "" : $"&cursor={cursor}");
            using var page = JsonDocument.Parse(await client.GetStringAsync(url));
            var items = page.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(pageSizes[pageIndex], items.Length);
            Assert.All(items, item => Assert.Equal(ordinaryId, item.GetProperty("sessionId").GetInt64()));
            var ids = items.Select(item => item.GetProperty("id").GetInt64()).ToArray();
            Assert.Equal(ids.OrderByDescending(id => id), ids);
            collected.AddRange(ids);
            cursor = page.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : page.RootElement.GetProperty("nextCursor").GetInt64();
            Assert.Equal(pageIndex < pageSizes.Length - 1, cursor is not null);
        }

        Assert.Equal(1001, collected.Count);
        Assert.Equal(1001, collected.Distinct().Count());
        using var sessionList = JsonDocument.Parse(await client.GetStringAsync("/api/sessions?limit=500"));
        var summary = sessionList.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetInt64() == ordinaryId);
        Assert.Equal(1001, summary.GetProperty("operations").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/sessions/{ordinaryId}/operations?cursor=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/sessions/{ordinaryId}/operations?cursor=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/sessions/{ordinaryId}/operations?cursor=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/sessions/{ordinaryId}/operations?limit=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sessions/987654/operations")).StatusCode);

        using var defaultLimit = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{ordinaryId}/operations"));
        Assert.Equal(500, defaultLimit.RootElement.GetProperty("items").GetArrayLength());
        using var clamped = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{ordinaryId}/operations?limit=9000"));
        Assert.Equal(500, clamped.RootElement.GetProperty("items").GetArrayLength());
        using var minimum = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{ordinaryId}/operations?limit=0"));
        Assert.Single(minimum.RootElement.GetProperty("items").EnumerateArray());
        using var emptyPage = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{emptyId}/operations"));
        Assert.Empty(emptyPage.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, emptyPage.RootElement.GetProperty("nextCursor").ValueKind);
        var reader = new SqlHarness.Dashboard.JournalReader(home.DatabasePath, new FakeProcesses());
        Assert.Null(reader.SessionOperations(987654));
        Assert.Empty(reader.SessionOperations(emptyId)!.Items);

        using var connection = new SqliteConnection($"Data Source={home.DatabasePath};Pooling=False");
        connection.Open();
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM operations WHERE session_id = $session AND id < $cursor;";
            delete.Parameters.AddWithValue("$session", ordinaryId);
            delete.Parameters.AddWithValue("$cursor", collected[499]);
            delete.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO operations (session_id, operation, host_pid, started_at, updated_at, finished_at, status)
                VALUES ($session, 'new-insert', 5151, '2026-10-07T09:00:00.000Z', '2026-10-07T09:00:00.000Z',
                    '2026-10-07T09:00:00.000Z', 'succeeded');
                """;
            insert.Parameters.AddWithValue("$session", ordinaryId);
            insert.ExecuteNonQuery();
        }

        using var afterRetention = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/sessions/{ordinaryId}/operations?cursor={collected[499]}&limit=500"));
        Assert.Empty(afterRetention.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, afterRetention.RootElement.GetProperty("nextCursor").ValueKind);
    }

    private static void InsertOperations(string databasePath, long sessionId, int count)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operations (session_id, operation, host_pid, started_at, updated_at, finished_at, status)
            VALUES ($session, 'query', 5151, '2026-10-07T09:00:00.000Z',
                '2026-10-07T09:00:00.000Z', '2026-10-07T09:00:00.000Z', 'succeeded');
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        for (var index = 0; index < count; index++)
            command.ExecuteNonQuery();

        transaction.Commit();
    }
}
