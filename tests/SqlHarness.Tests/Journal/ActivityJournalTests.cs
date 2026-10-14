using System.Diagnostics;
using System.Runtime.Versioning;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Tests;

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

    [Fact]
    public void Compare_context_and_conditions_follow_journal_sensitivity()
    {
        using var hashOnly = new JournalTempDirectory();
        using var sensitive = new JournalTempDirectory();
        var operation = new SqlHarnessCompareOperation(
            new SqlTargetRequest("local", new Dictionary<string, string> { ["tenant"] = "acme" }),
            "CREATE TABLE #t (value nvarchar(20)); -- SQLH_SETUP_MARKER",
            "SELECT @id", "SELECT @id", ["id:nvarchar=SQLH_VALUE_MARKER"], 30, 5,
            ResultComparisonMode.Multiset, true)
        {
            Context = new CompareContext("orders-report", "whole"),
        };
        var start = OperationJournalDescriber.DescribeStart(operation);

        var hashOnlyJournal = Open(hashOnly, TextWriter.Null);
        hashOnlyJournal.Begin(JournalTestData.Session(), start);
        var sensitiveJournal = Open(sensitive, TextWriter.Null, storeSensitive: true);
        sensitiveJournal.Begin(JournalTestData.Session(), start);

        var hashOnlyRow = JournalDb.Rows(hashOnly.DatabasePath,
            "SELECT compare_subject, compare_scope, compare_conditions_json FROM operations").Single();
        Assert.Equal("orders-report", hashOnlyRow["compare_subject"]);
        Assert.Equal("whole", hashOnlyRow["compare_scope"]);
        var hashOnlyBytes = JournalDb.AllBytes(hashOnly.DatabasePath);
        Assert.False(JournalDb.Contains(hashOnlyBytes, "SQLH_SETUP_MARKER"));
        Assert.False(JournalDb.Contains(hashOnlyBytes, "SQLH_VALUE_MARKER"));
        Assert.True(JournalDb.Contains(hashOnlyBytes, "Multiset"));
        Assert.True(JournalDb.Contains(hashOnlyBytes, "orders-report"));

        var matrix = new SqlHarnessCompareMatrixOperation(
            new SqlTargetRequest("local", new Dictionary<string, string>()), null,
            "SELECT @tenant", "SELECT @tenant", [], 20, 3, "tenant:nvarchar=SQLH_CLI_MATRIX_MARKER")
        {
            Context = new CompareContext("orders-report", "history"),
        };
        var matrixStart = OperationJournalDescriber.DescribeStart(matrix);
        hashOnlyJournal.Begin(JournalTestData.Session(), matrixStart);
        sensitiveJournal.Begin(JournalTestData.Session(), matrixStart);
        var matrixRow = JournalDb.Rows(hashOnly.DatabasePath,
            "SELECT compare_subject, compare_scope, compare_conditions_json FROM operations ORDER BY id DESC LIMIT 1").Single();
        Assert.Equal("orders-report", matrixRow["compare_subject"]);
        Assert.Equal("history", matrixRow["compare_scope"]);
        Assert.DoesNotContain("SQLH_CLI_MATRIX_MARKER", (string)matrixRow["compare_conditions_json"]!);
        Assert.False(JournalDb.Contains(JournalDb.AllBytes(hashOnly.DatabasePath), "SQLH_CLI_MATRIX_MARKER"));

        var sensitiveBytes = JournalDb.AllBytes(sensitive.DatabasePath);
        Assert.True(JournalDb.Contains(sensitiveBytes, "SQLH_SETUP_MARKER"));
        Assert.True(JournalDb.Contains(sensitiveBytes, "SQLH_VALUE_MARKER"));
        Assert.True(JournalDb.Contains(sensitiveBytes, "SQLH_CLI_MATRIX_MARKER"));
        Assert.Equal(8L, JournalDb.Rows(sensitive.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
    }

    [Fact]
    public void Oversized_sensitive_conditions_remain_valid_bounded_json_with_omission_metadata()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null, storeSensitive: true);
        var operation = new SqlHarnessCompareOperation(
            new SqlTargetRequest("local", new Dictionary<string, string>()),
            new string('\0', 12_000), "SELECT @id", "SELECT @id", ["id:int=SQLH_RETAIN_MARKER"], 30, 5);
        var start = OperationJournalDescriber.DescribeStart(operation);

        journal.Begin(JournalTestData.Session(), start);

        var jsonText = (string)JournalDb.Rows(temp.DatabasePath,
            "SELECT compare_conditions_json FROM operations").Single()["compare_conditions_json"]!;
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(jsonText) <= 64 * 1024);
        using var json = System.Text.Json.JsonDocument.Parse(jsonText);
        var root = json.RootElement;
        Assert.Equal(30, root.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(5, root.GetProperty("repeat").GetInt32());
        Assert.Equal("Ordered", root.GetProperty("comparisonMode").GetString());
        Assert.Contains("setupSql", root.GetProperty("omittedSensitiveFields").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("SQLH_RETAIN_MARKER", root.GetProperty("parameters").GetRawText());
        Assert.False(root.TryGetProperty("setupSql", out _));
    }

    [Fact]
    public void Known_compare_context_survives_rejected_and_failed_completion()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);
        var context = new CompareContext("orders-report", "aggregation");
        var rejected = journal.Begin(JournalTestData.Session(), JournalTestData.Start() with
        {
            Operation = "compare",
            Context = context,
        });
        var failed = journal.Begin(JournalTestData.Session(), JournalTestData.Start() with
        {
            Operation = "compare",
            Context = context with { Scope = "whole" },
        });

        Assert.True(journal.Complete(rejected, new OperationEnd("rejected", 2, "safety_rejected", 1,
            null, null, null, null, null)));
        Assert.True(journal.Complete(failed, new OperationEnd("failed", 5, "sql_execution_failed", 1,
            null, null, null, null, null)));

        var rows = JournalDb.Rows(temp.DatabasePath,
            "SELECT status, compare_subject, compare_scope FROM operations ORDER BY id");
        Assert.Equal([("rejected", "orders-report", "aggregation"), ("failed", "orders-report", "whole")],
            rows.Select(row => ((string)row["status"]!, (string)row["compare_subject"]!, (string)row["compare_scope"]!)).ToArray());
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
        Assert.Equal(400L, done["raw_bytes"]);
        Assert.Equal(40L, done["emitted_bytes"]);
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

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Database_and_directory_are_owner_only_on_unix()
    {
        using var temp = new JournalTempDirectory();

        Open(temp, TextWriter.Null).Begin(JournalTestData.Session(), JournalTestData.Start());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp.DatabasePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(temp.DatabasePath)!));
    }

    [Fact]
    public void Redacted_error_message_is_stored_by_default()
    {
        using var defaultJournal = new JournalTempDirectory();
        using var sensitive = new JournalTempDirectory();
        var end = JournalTestData.End("failed", 5) with { ErrorKind = "sql_execution_failed", ErrorMessage = "Invalid object name '[REDACTED]'." };

        var plain = Open(defaultJournal, TextWriter.Null);
        plain.Complete(plain.Begin(JournalTestData.Session(), JournalTestData.Start()), end);
        var stored = Open(sensitive, TextWriter.Null, storeSensitive: true);
        stored.Complete(stored.Begin(JournalTestData.Session(), JournalTestData.Start()), end);

        Assert.Equal("Invalid object name '[REDACTED]'.",
            JournalDb.Rows(defaultJournal.DatabasePath, "SELECT error_message FROM operations")[0]["error_message"]);
        Assert.Equal("Invalid object name '[REDACTED]'.",
            JournalDb.Rows(sensitive.DatabasePath, "SELECT error_message FROM operations")[0]["error_message"]);
    }

    [Fact]
    public void Long_error_message_is_truncated()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);
        var end = JournalTestData.End("failed", 5) with { ErrorMessage = new string('x', 5000) };

        journal.Complete(journal.Begin(JournalTestData.Session(), JournalTestData.Start()), end);

        var message = (string)JournalDb.Rows(temp.DatabasePath, "SELECT error_message FROM operations")[0]["error_message"]!;
        Assert.Equal(ActivityJournal.ErrorMessageLimit, message.Length);
        Assert.EndsWith("\u2026", message);
    }

    [Fact]
    public void Version_3_journal_migrates_to_current_and_keeps_rows()
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

        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        var row = JournalDb.Rows(temp.DatabasePath, "SELECT error_kind, error_message FROM operations")[0];
        Assert.Equal("sql_execution_failed", row["error_kind"]);
        Assert.Null(row["error_message"]);
    }

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

        Assert.Equal(8L, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        var session = JournalDb.Rows(temp.DatabasePath, "SELECT client_title, roots_json FROM sessions")[0];
        Assert.Null(session["client_title"]);
        Assert.Null(session["roots_json"]);
        var operation = JournalDb.Rows(temp.DatabasePath,
            "SELECT cancel_reason, compare_subject, compare_scope, compare_conditions_json FROM operations")[0];
        Assert.Null(operation["cancel_reason"]);
        Assert.Null(operation["compare_subject"]);
        Assert.Null(operation["compare_scope"]);
        Assert.Null(operation["compare_conditions_json"]);
    }

    [Fact]
    public void Version_6_journal_migrates_to_v8_without_inventing_legacy_provenance_or_changing_bytes()
    {
        using var temp = new JournalTempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.DatabasePath)!);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = JournalSchema.Version1 + JournalSchema.Version2 + JournalSchema.Version3
                + JournalSchema.Version4 + JournalSchema.Version5 + JournalSchema.Version6 + """
                INSERT INTO sessions (session_key, agent_kind, transport, source, host_pid, first_seen, last_seen)
                VALUES ('cli:v6', 'agent', 'cli', 'process-tree', 1, 't', 't');
                INSERT INTO operations (session_id, operation, host_pid, started_at, updated_at, status, raw_bytes, emitted_bytes)
                VALUES (1, 'compare', 1, 't', 't', 'succeeded', 1234, 567);
                PRAGMA user_version = 6;
                """;
            command.ExecuteNonQuery();
        }

        Open(temp, TextWriter.Null);

        Assert.Equal(8L, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        var row = JournalDb.Rows(temp.DatabasePath,
            "SELECT operation_ref, parent_operation_ref, dry_run, raw_bytes, emitted_bytes, compare_subject, compare_scope, compare_conditions_json FROM operations").Single();
        Assert.Null(row["operation_ref"]);
        Assert.Null(row["parent_operation_ref"]);
        Assert.Equal(0L, row["dry_run"]);
        Assert.Equal(1234L, row["raw_bytes"]);
        Assert.Equal(567L, row["emitted_bytes"]);
        Assert.Null(row["compare_subject"]);
        Assert.Null(row["compare_scope"]);
        Assert.Null(row["compare_conditions_json"]);
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
    public void Session_protocol_version_keeps_first_non_null_value()
    {
        using var temp = new JournalTempDirectory();
        var journal = Open(temp, TextWriter.Null);
        var session = JournalTestData.Session("mcp:proto");

        journal.Begin(session with { ProtocolVersion = null }, JournalTestData.Start());
        journal.Begin(session with { ProtocolVersion = "2026-07-28" }, JournalTestData.Start());
        journal.Begin(session with { ProtocolVersion = "2025-11-25" }, JournalTestData.Start());

        Assert.Equal("2026-07-28", JournalDb.Rows(temp.DatabasePath, "SELECT protocol_version FROM sessions").Single()["protocol_version"]);
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
}