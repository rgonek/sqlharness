using SqlHarness.Core;
using SqlHarness.Tests.Journal;

namespace SqlHarness.Tests.Dashboard;

internal sealed class JournalSeed
{
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly IActivityJournal _journal;

    public JournalSeed(string databasePath, bool storeSensitive = false) =>
        _journal = ActivityJournal.Open(databasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, _time);

    public static readonly DateTimeOffset HostStarted = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    public static SessionIdentity Session(string key, string agent = "claude", int hostPid = 5151) => new(
        key, agent, "process-tree", JournalTransport.Cli, null, null, null, 4242, HostStarted.AddMinutes(-5), hostPid, HostStarted, "/work");

    public JournalHandle Operation(
        SessionIdentity session, string operation = "query", string status = "succeeded", int exitCode = 0,
        string sql = "SELECT 1", long durationMs = 10, BenchmarkJournalRecord? benchmark = null, bool complete = true,
        SeedTokens tokens = SeedTokens.Both, string? profile = "local", IReadOnlyDictionary<string, string>? variables = null,
        string engine = "sqlserver", string server = "srv", string database = "db",
        string? errorKind = null, string? cancelReason = null, AgentCallMetadata? agent = null)
    {
        variables ??= new Dictionary<string, string> { ["tenant"] = "acme" };
        var handle = _journal.Begin(session, new OperationStart(operation, profile, variables,
            false, OperationJournalDescriber.SqlHash(sql), null, sql, null, agent))!;
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

    public static BenchmarkJournalRecord Benchmark(long readsMedian = 5, long physical = 0, long granted = 4096, long used = 256) =>
        new(
        [
            new JournalVariantMetrics("measure", null, null, 3, new CompareDistribution(10, 12, 14), new CompareDistribution(8, 9, 10),
                new CompareDistribution(readsMedian, readsMedian, readsMedian), 4096, granted, used, 4, 12, 10, 1, true, true, 1,
                [new JournalWait("PAGEIOLATCH_SH", 40, 7)], null,
                [new JournalTableIo("Orders", readsMedian, 1, physical, 0, 0, 0, 0, 0, physical > 0 ? 1 : 0)],
                [new JournalPlanLink(1, 0, new string('A', 64))]),
        ],
        [new JournalPlanDocument(new string('A', 64), "showplan-xml", Journal.PlanMetricsExtractorTests.ActualPlan)]);

    public static void Annotate(string databasePath, string sessionKey, string? title = null, string? note = null,
        bool starred = false, bool archived = false)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
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

    public static void Link(string databasePath, long operationId, string kind, string conversationId, string? title,
        string? model = null, string? effort = null)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
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
