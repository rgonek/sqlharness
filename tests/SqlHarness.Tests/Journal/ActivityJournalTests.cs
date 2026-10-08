using System.Diagnostics;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class ActivityJournalTests
{
    private static IActivityJournal Open(JournalTempDirectory temp, TextWriter log, bool storeSensitive = false, FixedTimeProvider? time = null) =>
        ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, log,
            time ?? new FixedTimeProvider(JournalTestData.T0));

    [Fact]
    public void Open_creates_current_schema_in_wal_mode()
    {
        using var temp = new JournalTempDirectory();

        Assert.IsType<ActivityJournal>(Open(temp, TextWriter.Null));

        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        Assert.Equal("wal", JournalDb.Rows(temp.DatabasePath, "PRAGMA journal_mode")[0]["journal_mode"]);
    }

    [Fact]
    public void Disabled_config_returns_null_journal_and_creates_no_file()
    {
        using var temp = new JournalTempDirectory();

        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { Enabled = false }, TextWriter.Null, TimeProvider.System);

        Assert.Same(NullActivityJournal.Instance, journal);
        Assert.False(File.Exists(temp.DatabasePath));
    }

    [Fact]
    public void Begin_complete_and_emission_write_one_operation_row()
    {
        using var temp = new JournalTempDirectory();
        var time = new FixedTimeProvider(JournalTestData.T0);
        var journal = Open(temp, TextWriter.Null, time: time);

        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        Assert.NotNull(handle);
        var running = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operations")[0];
        Assert.Equal("running", running["status"]);
        Assert.Equal("query", running["operation"]);
        Assert.Equal("local", running["profile"]);
        Assert.Equal("{\"tenant\":\"acme\"}", running["vars_json"]);
        Assert.Equal("sha256:abc", running["sql_hash"]);
        Assert.Null(running["sql_text"]);
        Assert.Equal("2026-10-06T09:00:00.000Z", running["started_at"]);

        time.Now = JournalTestData.T0.AddSeconds(2);
        journal.Complete(handle, JournalTestData.End());
        journal.RecordEmission(handle, new OutputFootprint(400, 10), new OutputFootprint(40, 2));

        var done = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operations")[0];
        Assert.Equal("succeeded", done["status"]);
        Assert.Equal(0L, done["exit_code"]);
        Assert.Equal("srv", done["server"]);
        Assert.Equal(3L, done["rows_returned"]);
        Assert.Equal("2026-10-06T09:00:02.000Z", done["finished_at"]);
        Assert.Equal(100L, done["raw_tokens"]);
        Assert.Equal(10L, done["emitted_tokens"]);
        var session = JournalDb.Rows(temp.DatabasePath, "SELECT * FROM sessions")[0];
        Assert.Equal("claude", session["agent_kind"]);
        Assert.Equal("cli", session["transport"]);
        Assert.Equal("2026-10-06T09:00:02.000Z", session["last_seen"]);
    }

    [Fact]
    public void Sql_text_is_stored_only_when_store_sensitive()
    {
        using var hashOnly = new JournalTempDirectory();
        using var sensitive = new JournalTempDirectory();

        Open(hashOnly, TextWriter.Null).Begin(JournalTestData.Session(), JournalTestData.Start("SELECT 'SQLH_SQL_MARKER'"));
        Open(sensitive, TextWriter.Null, storeSensitive: true).Begin(JournalTestData.Session(), JournalTestData.Start("SELECT 'SQLH_SQL_MARKER'"));

        Assert.False(JournalDb.Contains(JournalDb.AllBytes(hashOnly.DatabasePath), "SQLH_SQL_MARKER"));
        Assert.True(JournalDb.Contains(JournalDb.AllBytes(sensitive.DatabasePath), "SQLH_SQL_MARKER"));
    }

    [Fact]
    public void Parallel_begins_from_one_session_share_one_session_row()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);

        Parallel.For(0, 8, _ => Assert.NotNull(journal.Begin(JournalTestData.Session(), JournalTestData.Start())));

        Assert.Single(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM sessions"));
        Assert.Equal(8, JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operations").Count);
    }

    [Fact]
    public void Begin_returns_null_quickly_when_the_database_write_lock_is_held()
    {
        using var temp = new JournalTempDirectory();
        var log = new StringWriter();
        var journal = Open(temp, log);
        using var holder = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        var stopwatch = Stopwatch.StartNew();
        var first = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        var second = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        stopwatch.Stop();

        Assert.Null(first);
        Assert.Null(second);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.DoesNotContain(temp.Path, lines[0]);
    }

    [Fact]
    public void Broken_log_writer_never_throws_out_of_the_journal()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, ThrowingWriter.Instance);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());
        Assert.NotNull(handle);
        using var holder = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        Assert.Null(journal.Begin(JournalTestData.Session(), JournalTestData.Start()));
        Assert.False(journal.Complete(handle, JournalTestData.End()));
        journal.RecordEmission(handle, null, new OutputFootprint(1, 1));
    }

    [Fact]
    public void Broken_log_writer_never_throws_out_of_open()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(temp.DatabasePath); // a directory where the database file should be: unavailable

        Assert.Same(NullActivityJournal.Instance, Open(temp, ThrowingWriter.Instance));
    }

    [Fact]
    public void Complete_reports_whether_the_row_was_written()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start());

        Assert.True(journal.Complete(handle, JournalTestData.End()));
        Assert.False(journal.Complete(null, JournalTestData.End()));
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public static readonly ThrowingWriter Instance = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => throw new IOException("stderr closed");
        public override void Write(string? value) => throw new IOException("stderr closed");
        public override void WriteLine(string? value) => throw new IOException("stderr closed");
    }

    [Fact]
    public void Complete_and_emission_with_null_handle_are_no_ops()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);

        journal.Complete(null, JournalTestData.End());
        journal.RecordEmission(null, null, new OutputFootprint(1, 1));

        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operations"));
    }

    [Fact]
    public void Corrupt_database_is_quarantined_and_recreated()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        File.WriteAllBytes(temp.DatabasePath, Enumerable.Repeat((byte)0x5A, 4096).ToArray());

        var journal = Open(temp, TextWriter.Null);

        Assert.IsType<ActivityJournal>(journal);
        Assert.NotNull(journal.Begin(JournalTestData.Session(), JournalTestData.Start()));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(temp.DatabasePath)!, "activity.db.corrupt-*"));
    }

    [Fact]
    public void Newer_schema_disables_the_journal_without_writing()
    {
        using var temp = new JournalTempDirectory();
        Open(temp, TextWriter.Null);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var log = new StringWriter();
        var journal = Open(temp, log);

        Assert.Same(NullActivityJournal.Instance, journal);
        Assert.Contains("newer", log.ToString());
        Assert.Equal(99L, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
    }

    [Fact]
    public void Database_and_directory_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var temp = new JournalTempDirectory();

        Open(temp, TextWriter.Null).Begin(JournalTestData.Session(), JournalTestData.Start());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp.DatabasePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(temp.DatabasePath)!));
    }

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
        Assert.EndsWith("\u2026", message);
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
}