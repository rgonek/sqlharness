using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class MutationJournalTests
{
    private static IActivityJournal Open(JournalTempDirectory temp, bool storeSensitive = false, FixedTimeProvider? time = null) =>
        ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null,
            time ?? new FixedTimeProvider(JournalTestData.T0));

    private static MutationJournalRecord Record(string requestId, DateTimeOffset? createdAt = null) => new(
        requestId, "mcp:caller", "local", "postgres", "db.example", "app", "dml", "operator", "digest-" + requestId,
        "UPDATE accounts SET secret = 'marker'", "{\"secret\":\"marker\"}", createdAt ?? JournalTestData.T0);

    [Fact]
    public void Version_12_journal_migrates_to_current_and_creates_mutation_requests()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = JournalSchema.Version1 + JournalSchema.Version2 + JournalSchema.Version3
                + JournalSchema.Version4 + JournalSchema.Version5 + JournalSchema.Version6 + JournalSchema.Version7
                + JournalSchema.Version8 + JournalSchema.Version9 + JournalSchema.Version10 + JournalSchema.Version11
                + JournalSchema.Version12 + "PRAGMA user_version = 12;";
            command.ExecuteNonQuery();
        }

        Open(temp);

        Assert.Equal(13, JournalSchema.CurrentVersion);
        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version").Single()["user_version"]);
        Assert.Equal(new[] { "id", "request_id", "session_key", "profile", "engine", "server", "database_name", "tier", "mode", "digest", "sql_text",
            "parameters_json", "state", "status", "verification", "created_at", "decided_at", "finished_at" },
            JournalDb.Rows(temp.DatabasePath, "SELECT name FROM pragma_table_info('mutation_requests') ORDER BY cid")
                .Select(row => (string)row["name"]!).ToArray());
        Assert.Single(JournalDb.Rows(temp.DatabasePath,
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'ix_mutation_requests_created'"));
    }

    [Fact]
    public void Record_and_update_mutation_preserve_lifecycle_fields()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        var record = Record("request-1");

        journal.RecordMutation(record);
        journal.UpdateMutation(record.RequestId, "approved", null, "click", record.CreatedAt.AddSeconds(1));
        journal.UpdateMutation(record.RequestId, "finished", "success", null, record.CreatedAt.AddSeconds(2));

        var row = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM mutation_requests").Single();
        Assert.Equal("request-1", row["request_id"]);
        Assert.Equal("finished", row["state"]);
        Assert.Equal("success", row["status"]);
        Assert.Equal("click", row["verification"]);
        Assert.Equal(record.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), row["created_at"]);
        Assert.Equal(record.CreatedAt.AddSeconds(1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), row["decided_at"]);
        Assert.Equal(record.CreatedAt.AddSeconds(2).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), row["finished_at"]);
        Assert.Equal(record.Sql, row["sql_text"]);
        Assert.Equal(record.ParametersJson, row["parameters_json"]);
    }

    [Fact]
    public void Mutation_without_operator_verification_records_decision_time()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp);
        var record = Record("automatic");
        journal.RecordMutation(record);

        journal.UpdateMutation(record.RequestId, "executing", "success", null, record.CreatedAt.AddSeconds(3));

        var row = JournalDb.Rows(temp.DatabasePath, "SELECT decided_at, finished_at FROM mutation_requests").Single();
        Assert.Equal(record.CreatedAt.AddSeconds(3).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), row["decided_at"]);
        Assert.Equal(record.CreatedAt.AddSeconds(3).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), row["finished_at"]);
    }

    [Fact]
    public void Mutation_sql_and_parameters_are_not_stored_without_sensitive_journaling()
    {
        using var temp = new JournalTempDirectory();
        var record = Record("request-1");

        Open(temp).RecordMutation(record);

        var row = JournalDb.Rows(temp.DatabasePath, "SELECT sql_text, parameters_json FROM mutation_requests").Single();
        Assert.Null(row["sql_text"]);
        Assert.Null(row["parameters_json"]);
    }

    [Fact]
    public void Mutation_history_is_bounded_and_uses_a_stable_cursor()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        journal.RecordMutation(Record("request-1", JournalTestData.T0));
        journal.RecordMutation(Record("request-2", JournalTestData.T0.AddSeconds(1)));
        journal.RecordMutation(Record("request-3", JournalTestData.T0.AddSeconds(2)));

        var reader = new SqlHarness.Dashboard.JournalReader(temp.DatabasePath, SqlHarness.Core.ProcessInfo.Current);
        var first = reader.MutationRequests(limit: 2);
        var second = reader.MutationRequests(first.NextCursor, limit: 2);

        Assert.Equal(new[] { "request-3", "request-2" }, first.Items.Select(item => item.RequestId));
        Assert.Equal(2L, first.NextCursor);
        Assert.Equal(new[] { "request-1" }, second.Items.Select(item => item.RequestId));
        Assert.Null(second.NextCursor);
        Assert.Equal("UPDATE accounts SET secret = 'marker'", second.Items.Single().Sql);
    }

    [Fact]
    public void Retention_deletes_old_mutation_requests()
    {
        using var temp = new JournalTempDirectory();
        var clock = new FixedTimeProvider(JournalTestData.T0);
        var journal = Open(temp, storeSensitive: true, clock);
        journal.RecordMutation(Record("old", JournalTestData.T0.AddDays(-40)));
        journal.RecordMutation(Record("new", JournalTestData.T0.AddDays(-1)));

        var result = JournalRetention.Run(temp.DatabasePath,
            new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 30 } },
            ProcessInfo.Current, clock);

        Assert.Equal(1, result.DeletedMutationRequests);
        Assert.Equal(new[] { "new" }, JournalDb.Rows(temp.DatabasePath, "SELECT request_id FROM mutation_requests").Select(row => (string)row["request_id"]!));
    }

    [Fact]
    public void Retention_deletes_oldest_mutation_requests_when_the_size_limit_is_exceeded()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        journal.RecordMutation(Record("oldest") with { Sql = new string('x', 600_000) });
        journal.RecordMutation(Record("newest", JournalTestData.T0.AddSeconds(1)) with { Sql = new string('y', 600_000) });

        var result = JournalRetention.Run(temp.DatabasePath,
            new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 3650, MaxSizeMb = 1 } },
            ProcessInfo.Current, new FixedTimeProvider(JournalTestData.T0));

        Assert.True(result.DeletedMutationRequests > 0);
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM mutation_requests"));
    }
}