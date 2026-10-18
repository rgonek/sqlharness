using System.Globalization;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Tests.Journal;

namespace SqlHarness.Tests.Dashboard;

internal sealed class JournalSeed
{
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly IActivityJournal _journal;
    private readonly string _databasePath;

    public JournalSeed(string databasePath, bool storeSensitive = false)
    {
        _databasePath = databasePath;
        _journal = ActivityJournal.Open(databasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, _time);
    }

    public static readonly DateTimeOffset HostStarted = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    public static SessionIdentity Session(string key, string agent = "claude", int hostPid = 5151) => new(
        key, agent, "process-tree", JournalTransport.Cli, null, null, null, 4242, HostStarted.AddMinutes(-5), hostPid, HostStarted, "/work");

    public JournalHandle Operation(
        SessionIdentity session, string operation = "query", string status = "succeeded", int exitCode = 0,
        string sql = "SELECT 1", long durationMs = 10, BenchmarkJournalRecord? benchmark = null, bool complete = true,
        SeedTokens tokens = SeedTokens.Both, string? profile = "local", IReadOnlyDictionary<string, string>? variables = null,
        string engine = "sqlserver", string server = "srv", string database = "db",
        string? errorKind = null, string? cancelReason = null, AgentCallMetadata? agent = null, string? setupSql = null)
    {
        variables ??= new Dictionary<string, string> { ["tenant"] = "acme" };
        var handle = _journal.Begin(session, new OperationStart(operation, profile, variables,
            false, OperationJournalDescriber.SqlHash(sql), null, sql, null, agent)
        {
            SetupSqlText = setupSql,
        })!;
        _time.Now = _time.Now.AddSeconds(1);
        if (complete)
        {
            _journal.Complete(handle, new OperationEnd(status, exitCode, errorKind ?? (status == "rejected" ? "safety" : null), durationMs,
                engine, server, database, 1, 3, tokens == SeedTokens.RawOnly ? 500 : null, null, null, CancelReason: cancelReason));
            if (tokens == SeedTokens.Both)
                _journal.RecordEmission(handle, new OutputFootprint(400, 1), new OutputFootprint(40, 1));
            else if (tokens == SeedTokens.EmittedOnly)
                _journal.RecordEmission(handle, null, new OutputFootprint(40, 1));
        }

        if (benchmark is not null)
            _journal.RecordBenchmark(handle, benchmark);
        _time.Now = _time.Now.AddSeconds(1);
        return handle;
    }

    public JournalHandle Compare(SessionIdentity session, string? subject, string? scope, string? baselineHash,
        string? candidateHash, string status = "succeeded", bool complete = true)
    {
        var start = new OperationStart("compare", "local", new Dictionary<string, string> { ["tenant"] = "acme" },
            false, baselineHash, candidateHash, null, null)
        {
            Context = subject is null || scope is null ? null : new CompareContext(subject, scope),
        };
        var handle = _journal.Begin(session, start)!;
        if (complete)
        {
            _journal.Complete(handle, new OperationEnd(status, status == "succeeded" ? 0 : 5, null, 10,
                "sqlserver", "srv", "db", 1, 3, null, null, null));
        }
        return handle;
    }

    public JournalHandle Measure(SessionIdentity session, string? subject, string? scope, string sqlHash,
        ConditionsModel conditions, BenchmarkJournalRecord metrics)
    {
        var start = new OperationStart("measure", "local", new Dictionary<string, string> { ["tenant"] = "acme" },
            false, sqlHash, null, "SELECT 1", null)
        {
            Context = subject is null || scope is null ? null : new CompareContext(subject, scope),
        };
        var handle = _journal.Begin(session, start)!;
        _journal.Complete(handle, new OperationEnd("succeeded", 0, null, 10,
            "sqlserver", "srv", "db", 1, 3, null, null, null));
        _journal.RecordConditions(handle, conditions);
        _journal.RecordBenchmark(handle, metrics);
        return handle;
    }

    public void RecordConditions(JournalHandle handle, ConditionsModel? conditions = null) =>
        _journal.RecordConditions(handle, conditions ?? ReproConditions());

    public void CompareEvidence(JournalHandle handle, ConditionsModel conditions, string summaryJson,
        BenchmarkJournalRecord metrics)
    {
        _journal.RecordConditions(handle, conditions);
        _journal.RecordBenchmark(handle, metrics);
        using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE operations SET summary_json = $summary WHERE id = $id;";
        update.Parameters.AddWithValue("$summary", summaryJson);
        update.Parameters.AddWithValue("$id", handle.OperationId);
        Assert.Equal(1, update.ExecuteNonQuery());
    }

    public static ConditionsModel ReproConditions() => new(
        1, "sqlserver", "srv", "db",
        new ConditionsExecution("measure", "new-session-per-case", "bound-per-case", 0, 1, 30, null, null),
        null, [], [new ConditionsCase("0", 0, "Case 1 of 1", null, [], "fingerprint")], "fingerprint", []);

    public static BenchmarkJournalRecord Benchmark(long readsMedian = 5, long physical = 0, long granted = 4096, long used = 256) =>
        new(
        [
            new JournalVariantMetrics("measure", null, null, 3, new CompareDistribution(10, 12, 14), new CompareDistribution(8, 9, 10),
                new CompareDistribution(readsMedian, readsMedian, readsMedian), 4096, granted, used, 4, 12, 10, 1, true, true, 1,
                [new JournalWait("PAGEIOLATCH_SH", 40, 7)], null,
                [new JournalTableIo("Orders", readsMedian, 1, physical, 0, 0, 0, 0, 0, physical > 0 ? 1 : 0)],
                [new JournalPlanLink(1, 0, new string('A', 64))])
            {
                PlanOccurrences = [new JournalPlanOccurrence(1, 0, 0, new string('A', 64))],
            },
        ],
        [new JournalPlanDocument(new string('A', 64), new string('B', 64), "showplan-xml", Journal.PlanMetricsExtractorTests.ActualPlan)]);

    public static void Annotate(string databasePath, string sessionKey, string? title = null, string? note = null,
        bool starred = false, bool archived = false)
    {
        // Pooled: seeding a few hundred annotations through one non-pooled connection each
        // cost about 7.5 ms per call. TempHome clears the pools before deleting the file.
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_annotations (session_id, title, note, starred, archived_at, updated_at)
            SELECT id, $title, $note, $starred, $archived, '2026-10-07T09:00:00.000Z' FROM sessions WHERE session_key = $key;
            """;
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$starred", starred ? 1 : 0);
        command.Parameters.AddWithValue("$archived", archived ? "2026-10-07T09:00:00.000Z" : DBNull.Value);
        command.Parameters.AddWithValue("$key", sessionKey);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>
    /// Bulk seeds <paramref name="count"/> sessions with keys <c>cli:000</c> … <c>cli:{count-1:D3}</c>,
    /// each carrying one completed query operation, for reader tests that need volume but assert
    /// nothing about what the writer wrote.
    /// One pooled connection and one commit, like <see cref="Annotate"/>: <see cref="Operation"/> costs
    /// a fresh non-pooled connection and a commit per journal call, which measured about 35 ms per
    /// seeded operation here and 8.3 s for the 240 sessions of the starred cursor test.
    /// <paramref name="onSession"/> runs after the commit, because a callback that opens its own
    /// connection (<see cref="Annotate"/>, <see cref="Link"/>) cannot write while this transaction
    /// holds the journal's WAL write lock.
    /// </summary>
    public static void Operations(string databasePath, int count, Action<int, long>? onSession = null)
    {
        EnsureSchema(databasePath);
        var identity = Session($"cli:{0:D3}");
        var sessionIds = new long[count];
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using (var transaction = connection.BeginTransaction())
        {
            using var session = SessionInsert(connection, transaction, identity);
            using var operation = connection.CreateCommand();
            operation.Transaction = transaction;
            // The columns JournalReader selects. Writer columns left out stay null instead of
            // matching the writer: vars_json and the emission footprints stay empty here.
            operation.CommandText = """
                INSERT INTO operations (session_id, operation, host_pid, host_started_at, started_at, updated_at,
                    finished_at, status, exit_code, duration_ms, profile, engine, server, database,
                    result_sets, rows_returned, sql_hash)
                VALUES ($session, 'query', $hostPid, $hostStarted, $seen, $seen,
                    $seen, 'succeeded', 0, 10, 'local', 'sqlserver', 'srv', 'db', 1, 3, $sqlHash);
                """;
            operation.Parameters.AddWithValue("$session", 0L);
            operation.Parameters.AddWithValue("$hostPid", identity.HostPid);
            operation.Parameters.AddWithValue("$hostStarted", Timestamp(identity.HostStartedAt));
            operation.Parameters.AddWithValue("$seen", Timestamp(HostStarted));
            operation.Parameters.AddWithValue("$sqlHash", OperationJournalDescriber.SqlHash("SELECT 1"));
            for (var index = 0; index < count; index++)
            {
                var seen = Timestamp(HostStarted.AddSeconds(index));
                var sessionId = (long)Insert(session, $"cli:{index:D3}", seen)!;
                sessionIds[index] = sessionId;
                operation.Parameters["$session"].Value = sessionId;
                operation.Parameters["$seen"].Value = seen;
                operation.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        if (onSession is not null)
            for (var index = 0; index < count; index++)
                onSession(index, sessionIds[index]);
    }

    /// <summary>
    /// Bulk seeds <paramref name="count"/> completed compare attempts into one session, for reader
    /// tests that page retained history. Same pooled single-transaction shape as
    /// <see cref="Operations"/>. Subject and scope are fixed and the baseline hash repeats while every
    /// candidate is unique, so <see cref="CompareHistoryBuilder"/> labels them v0 for the baseline and
    /// one version per attempt for the candidates.
    /// </summary>
    public static void Compares(string databasePath, string sessionKey, int count)
    {
        EnsureSchema(databasePath);
        var identity = Session(sessionKey);
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var session = SessionInsert(connection, transaction, identity);
        var sessionId = (long)Insert(session, sessionKey, Timestamp(identity.HostStartedAt))!;
        using var operation = connection.CreateCommand();
        operation.Transaction = transaction;
        operation.CommandText = """
            INSERT INTO operations (session_id, operation, host_pid, host_started_at, started_at, updated_at,
                finished_at, status, exit_code, duration_ms, profile, engine, server, database,
                result_sets, rows_returned, compare_subject, compare_scope, sql_hash, candidate_sql_hash)
            VALUES ($session, 'compare', $hostPid, $hostStarted, $seen, $seen,
                $seen, 'succeeded', 0, 10, 'local', 'sqlserver', 'srv', 'db', 1, 3, 'orders', 'whole', 'base', $candidate);
            """;
        operation.Parameters.AddWithValue("$session", sessionId);
        operation.Parameters.AddWithValue("$hostPid", identity.HostPid);
        operation.Parameters.AddWithValue("$hostStarted", Timestamp(identity.HostStartedAt));
        operation.Parameters.AddWithValue("$seen", Timestamp(HostStarted));
        operation.Parameters.AddWithValue("$candidate", "candidate-0");
        for (var index = 0; index < count; index++)
        {
            operation.Parameters["$seen"].Value = Timestamp(HostStarted.AddSeconds(index));
            operation.Parameters["$candidate"].Value = $"candidate-{index}";
            operation.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Creates and migrates the journal so a bulk helper can seed a reader fixture on its own.</summary>
    private static void EnsureSchema(string databasePath) =>
        _ = ActivityJournal.Open(databasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(HostStarted));

    /// <summary>
    /// One prepared session insert reused for every row, carrying the writer's columns for a
    /// <see cref="Session"/> except <c>agent_pid</c> and <c>agent_started_at</c>, which the writer does
    /// populate and which no reader test selects today — a test that starts selecting them must add them here.
    /// </summary>
    private static SqliteCommand SessionInsert(SqliteConnection connection, SqliteTransaction transaction, SessionIdentity identity)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions (session_key, agent_kind, transport, source, host_pid, host_started_at, cwd,
                first_seen, last_seen)
            VALUES ($key, $kind, $transport, $source, $hostPid, $hostStarted, $cwd, $seen, $seen)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$key", identity.SessionKey);
        command.Parameters.AddWithValue("$kind", identity.AgentKind);
        command.Parameters.AddWithValue("$transport", identity.Transport == JournalTransport.Mcp ? "mcp" : "cli");
        command.Parameters.AddWithValue("$source", identity.Source);
        command.Parameters.AddWithValue("$hostPid", identity.HostPid);
        command.Parameters.AddWithValue("$hostStarted", Timestamp(identity.HostStartedAt));
        command.Parameters.AddWithValue("$cwd", (object?)identity.Cwd ?? DBNull.Value);
        command.Parameters.AddWithValue("$seen", Timestamp(HostStarted));
        return command;
    }

    private static object? Insert(SqliteCommand session, string sessionKey, object seen)
    {
        session.Parameters["$key"].Value = sessionKey;
        session.Parameters["$seen"].Value = seen;
        return session.ExecuteScalar();
    }

    /// <summary>The journal's timestamp format; the reader orders these strings, so it has to match.</summary>
    private static object Timestamp(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static void Link(string databasePath, long operationId, string kind, string conversationId, string? title,
        string? model = null, string? effort = null)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                INSERT INTO agent_conversations (agent_kind, conversation_id, title, updated_at)
                VALUES ($kind, $conversationId, $title, '2026-10-07T09:00:00.000Z')
                ON CONFLICT (agent_kind, conversation_id) DO UPDATE SET title = excluded.title;
                """;
            conversation.Parameters.AddWithValue("$kind", kind);
            conversation.Parameters.AddWithValue("$conversationId", conversationId);
            conversation.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            conversation.ExecuteNonQuery();
        }
        using (var link = connection.CreateCommand())
        {
            link.Transaction = transaction;
            link.CommandText = """
                INSERT OR REPLACE INTO operation_agent_links (operation_id, conversation, model, effort)
                SELECT $operationId, id, $model, $effort FROM agent_conversations
                WHERE agent_kind = $kind AND conversation_id = $conversationId;
                """;
            link.Parameters.AddWithValue("$operationId", operationId);
            link.Parameters.AddWithValue("$kind", kind);
            link.Parameters.AddWithValue("$conversationId", conversationId);
            link.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
            link.Parameters.AddWithValue("$effort", (object?)effort ?? DBNull.Value);
            Assert.Equal(1, link.ExecuteNonQuery());
        }
        transaction.Commit();
    }
}

/// <summary>Which token counts a seeded operation carries: both, raw only (MCP-style), or emitted only.</summary>
internal enum SeedTokens
{
    None,
    Both,
    RawOnly,
    EmittedOnly,
}
