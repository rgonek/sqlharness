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
    public void Fresh_schema_includes_mutation_requests_after_older_journal_backup()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE old_rows (id INTEGER PRIMARY KEY); INSERT INTO old_rows VALUES (1); PRAGMA user_version = 12;";
            command.ExecuteNonQuery();
        }

        Open(temp);

        Assert.Equal(26, JournalSchema.CurrentVersion);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(temp.DatabasePath)!, "activity.db.v12-*.bak"));
        Assert.Single(JournalDb.Rows(backup, "SELECT id FROM old_rows"));
        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version").Single()["user_version"]);
        Assert.Equal(new[] { "id", "request_id", "session_key", "host_pid", "host_started_at", "profile", "engine", "server", "database_name", "tier", "mode", "digest",
            "environment", "sql_text", "parameters_json", "state", "status", "verification", "created_at", "decided_at", "finished_at", "operation_id" },
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
    public void Mutation_history_keeps_the_environment_label_from_submission()
    {
        using var temp = new JournalTempDirectory();
        var record = Record("environment") with { Environment = "production" };

        Open(temp).RecordMutation(record);

        var summary = new SqlHarness.Dashboard.JournalReader(temp.DatabasePath, SqlHarness.Core.ProcessInfo.Current)
            .MutationRequests().Items.Single();
        Assert.Equal("production", summary.Environment);
    }

    [Fact]
    public void Record_mutation_stores_current_journal_process_ownership()
    {
        using var temp = new JournalTempDirectory();
        Open(temp).RecordMutation(Record("owned"));

        var row = JournalDb.Rows(temp.DatabasePath, "SELECT host_pid, host_started_at FROM mutation_requests").Single();

        Assert.Equal((long)Environment.ProcessId, row["host_pid"]);
        var current = ProcessInfo.Current.Get(Environment.ProcessId);
        Assert.Equal(current?.StartedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), row["host_started_at"]);
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
    public void Cancellation_update_does_not_overwrite_executing_or_terminal_lifecycle_state()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp);
        journal.RecordMutation(Record("executing"));
        journal.RecordMutation(Record("terminal"));
        journal.UpdateMutation("executing", "executing", "success", null, JournalTestData.T0.AddSeconds(1));
        journal.UpdateMutation("terminal", "succeeded", "success", null, JournalTestData.T0.AddSeconds(1));

        journal.UpdateMutation("executing", "cancelled", null, null, JournalTestData.T0.AddSeconds(2));
        journal.UpdateMutation("terminal", "cancelled", null, null, JournalTestData.T0.AddSeconds(2));

        var states = JournalDb.Rows(temp.DatabasePath, "SELECT request_id, state, status FROM mutation_requests ORDER BY request_id")
            .ToDictionary(row => (string)row["request_id"]!, row => (State: (string)row["state"]!, Status: (string?)row["status"]));
        Assert.Equal(("executing", "success"), states["executing"]);
        Assert.Equal(("succeeded", "success"), states["terminal"]);
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
    public void Retention_deletes_old_abandoned_and_terminal_requests_but_preserves_live_requests()
    {
        using var temp = new JournalTempDirectory();
        var clock = new FixedTimeProvider(JournalTestData.T0);
        var journal = Open(temp, storeSensitive: true, clock);
        journal.RecordMutation(Record("live-pending", JournalTestData.T0.AddDays(-40)));
        journal.RecordMutation(Record("live-executing", JournalTestData.T0.AddDays(-41)));
        journal.RecordMutation(Record("abandoned-approved", JournalTestData.T0.AddDays(-42)));
        journal.RecordMutation(Record("terminal", JournalTestData.T0.AddDays(-43)));
        SetOwnerAndState(temp, "live-pending", "pending", 101, JournalTestData.T0);
        SetOwnerAndState(temp, "live-executing", "executing", 101, JournalTestData.T0);
        SetOwnerAndState(temp, "abandoned-approved", "approved", 202, JournalTestData.T0);
        SetOwnerAndState(temp, "terminal", "succeeded", 101, JournalTestData.T0);

        var result = JournalRetention.Run(temp.DatabasePath,
            new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 30 } },
            new FakeProcesses(JournalTestData.T0, 101), clock);

        Assert.Equal(2, result.DeletedMutationRequests);
        Assert.Equal(new[] { "live-executing", "live-pending" }, JournalDb.Rows(temp.DatabasePath, "SELECT request_id FROM mutation_requests ORDER BY request_id")
            .Select(row => (string)row["request_id"]!));
    }

    [Fact]
    public void Retention_deletes_oldest_abandoned_and_terminal_requests_under_size_pressure_but_preserves_live_requests()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, storeSensitive: true);
        journal.RecordMutation(Record("live-pending") with { Sql = new string('x', 600_000) });
        journal.RecordMutation(Record("abandoned-executing", JournalTestData.T0.AddSeconds(1)) with { Sql = new string('y', 600_000) });
        journal.RecordMutation(Record("terminal", JournalTestData.T0.AddSeconds(2)) with { Sql = new string('z', 600_000) });
        SetOwnerAndState(temp, "live-pending", "pending", 101, JournalTestData.T0);
        SetOwnerAndState(temp, "abandoned-executing", "executing", 202, JournalTestData.T0);
        SetOwnerAndState(temp, "terminal", "succeeded", 101, JournalTestData.T0);

        var result = JournalRetention.Run(temp.DatabasePath,
            new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 3650, MaxSizeMb = 1 } },
            new FakeProcesses(JournalTestData.T0, 101), new FixedTimeProvider(JournalTestData.T0));

        Assert.Equal(2, result.DeletedMutationRequests);
        Assert.Equal(new[] { "live-pending" }, JournalDb.Rows(temp.DatabasePath, "SELECT request_id FROM mutation_requests")
            .Select(row => (string)row["request_id"]!));
    }

    private static void SetOwnerAndState(JournalTempDirectory temp, string requestId, string state, int hostPid, DateTimeOffset startedAt)
    {
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mutation_requests SET state = $state, host_pid = $pid, host_started_at = $started WHERE request_id = $id;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$pid", hostPid);
        command.Parameters.AddWithValue("$started", startedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        command.Parameters.AddWithValue("$id", requestId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private sealed class FakeProcesses(DateTimeOffset startedAt, params int[] livePids) : IProcessInfo
    {
        public int CurrentPid => 999;

        public ProcessSnapshot? Get(int pid) => livePids.Contains(pid)
            ? new ProcessSnapshot(pid, null, "test", startedAt, null)
            : null;
    }
}