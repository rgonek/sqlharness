using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Core.Journal;

using OperationPhase = SqlHarness.Core.Journal.OperationPhase;

namespace SqlHarness.Tests.Journal;

public sealed class ActivityJournalPhaseTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void Legacy_journals_are_backed_up_before_current_schema_is_created(int version)
    {
        using var temp = new JournalTempDirectory();
        CreateLegacyJournal(temp.DatabasePath, version);

        _ = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));

        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(temp.DatabasePath, "PRAGMA user_version")[0]["user_version"]);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(temp.DatabasePath)!, $"activity.db.v{version}-*.bak"));
        Assert.Equal("legacy-row", JournalDb.Rows(backup, "SELECT value FROM legacy_data").Single()["value"]);
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT id FROM operations"));
        Assert.Equal(2, JournalDb.Rows(temp.DatabasePath,
            "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('operation_phases', 'operation_events')").Count);
        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT * FROM session_annotations"));
    }

    [Fact]
    public void RecordPhases_writes_aggregates_events_and_reconciles_wall_duration()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start())!;
        PhaseAggregate[] phases =
        [
            new(OperationPhase.Connect, 2, 8, 5),
            new(OperationPhase.Execute, 1, 4, 4),
        ];
        PhaseEvent[] events =
        [
            new(3, 15, OperationPhase.Artifact, 4, "succeeded", null, "{\"fileCount\":3,\"path\":\"SQLH_SECRET_PATH\"}"),
            new(1, 1, OperationPhase.Connect, 5, "succeeded", null,
                "{\"clientConnectionId\":\"d85b1407-351d-4694-9392-03acc5870eb1\",\"authStrategy\":\"postgres\",\"attempt\":1}"),
            new(2, 8, OperationPhase.Connect, 3, "failed", 18456, null),
        ];

        journal.RecordPhases(handle, phases, events, droppedEvents: 0);
        Assert.True(journal.Complete(handle, JournalTestData.End() with { DurationMilliseconds = 20 }));

        var storedPhases = JournalDb.Rows(temp.DatabasePath,
            "SELECT phase, count, total_ms, max_ms FROM operation_phases ORDER BY phase");
        Assert.Equal(new[] { "connect|2|8|5", "execute|1|4|4", "other|1|8|8" },
            storedPhases.Select(row => $"{row["phase"]}|{row["count"]}|{row["total_ms"]}|{row["max_ms"]}"));
        var storedEvents = JournalDb.Rows(temp.DatabasePath,
            "SELECT seq, offset_ms, phase, duration_ms, outcome, error_number, detail_json FROM operation_events ORDER BY seq");
        Assert.Equal(new[] { 1L, 2L, 3L }, storedEvents.Select(row => (long)row["seq"]!));
        Assert.Equal("{\"clientConnectionId\":\"d85b1407-351d-4694-9392-03acc5870eb1\",\"authStrategy\":\"postgres\",\"attempt\":1}", storedEvents[0]["detail_json"]);
        Assert.Equal("{\"fileCount\":3}", storedEvents[2]["detail_json"]);
        Assert.False(JournalDb.Contains(JournalDb.AllBytes(temp.DatabasePath), "SQLH_SECRET_PATH"));
    }

    [Fact]
    public void Detail_persistence_keeps_valid_allowlisted_metadata_and_discards_unknown_fields()
    {
        var details = PersistEventDetails(
        [
            new(1, 1, OperationPhase.Connect, 2, "succeeded", null,
                """{"clientConnectionId":"d85b1407-351d-4694-9392-03acc5870eb1","authStrategy":"azure-cli","attempt":2,"sql":"SQLH_SECRET_SQL","user":"SQLH_SECRET_USER"}"""),
            new(2, 2, OperationPhase.Artifact, 1, "succeeded", null,
                """{"fileCount":7,"path":"SQLH_SECRET_PATH","nested":{"token":"SQLH_SECRET_TOKEN"}}"""),
            new(3, 3, OperationPhase.Connect, 1, "succeeded", null, """{"clientConnectionId":null,"attempt":1}"""),
            new(4, 4, OperationPhase.Connect, 1, "succeeded", null, """{"authStrategy":"ad-default","attempt":1}"""),
            new(5, 5, OperationPhase.Connect, 1, "succeeded", null, """{"authStrategy":"sql","attempt":1}"""),
        ]);

        Assert.Equal("""{"clientConnectionId":"d85b1407-351d-4694-9392-03acc5870eb1","authStrategy":"azure-cli","attempt":2}""", details[0]);
        Assert.Equal("""{"fileCount":7}""", details[1]);
        Assert.Equal("""{"clientConnectionId":null,"attempt":1}""", details[2]);
        Assert.Equal("""{"authStrategy":"ad-default","attempt":1}""", details[3]);
        Assert.Equal("""{"authStrategy":"sql","attempt":1}""", details[4]);
        Assert.All(details, detail => Assert.InRange(Assert.IsType<string>(detail).Length, 1, 512));
    }

    [Fact]
    public void Detail_persistence_discards_malformed_and_non_object_json()
    {
        var details = PersistEventDetails(
        [
            new(1, 1, OperationPhase.Connect, 1, "succeeded", null, "{\"attempt\":1"),
            new(2, 2, OperationPhase.Connect, 1, "succeeded", null, "[]"),
            new(3, 3, OperationPhase.Connect, 1, "succeeded", null, "\"text\""),
            new(4, 4, OperationPhase.Connect, 1, "succeeded", null, "null"),
        ]);

        Assert.All(details, detail => Assert.Null(detail));
    }

    [Fact]
    public void Detail_persistence_discards_invalid_values_for_known_fields()
    {
        var details = PersistEventDetails(
        [
            new(1, 1, OperationPhase.Connect, 1, "succeeded", null, """{"clientConnectionId":"not-a-guid"}"""),
            new(2, 2, OperationPhase.Connect, 1, "succeeded", null, """{"authStrategy":"integrated"}"""),
            new(3, 3, OperationPhase.Connect, 1, "succeeded", null, """{"attempt":0}"""),
            new(4, 4, OperationPhase.Connect, 1, "succeeded", null, """{"attempt":1.5}"""),
            new(5, 5, OperationPhase.Connect, 1, "succeeded", null, """{"attempt":-1}"""),
            new(6, 6, OperationPhase.Artifact, 1, "succeeded", null, """{"fileCount":"4"}"""),
            new(7, 7, OperationPhase.Artifact, 1, "succeeded", null, """{"fileCount":-1}"""),
        ]);

        Assert.All(details, detail => Assert.Null(detail));
    }

    [Fact]
    public void RecordPhases_writes_no_event_rows_when_capture_is_disabled()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start())!;

        journal.RecordPhases(handle, [], [], droppedEvents: 0);
        journal.Complete(handle, JournalTestData.End());

        Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT * FROM operation_events"));
    }

    [Fact]
    public void Dropped_events_end_with_a_truncation_row()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start())!;
        var events = Enumerable.Range(1, OperationPhaseRecorder.MaximumEvents)
            .Select(seq => new PhaseEvent(seq, seq, OperationPhase.Connect, 1, "succeeded", null, null))
            .ToArray();

        journal.RecordPhases(handle, [], events, droppedEvents: 3);
        journal.Complete(handle, JournalTestData.End());

        var rows = JournalDb.Rows(temp.DatabasePath,
            "SELECT seq, outcome, detail_json FROM operation_events ORDER BY seq");
        Assert.Equal(OperationPhaseRecorder.MaximumEvents + 1, rows.Count);
        Assert.Equal((257L, "truncated", "{\"dropped\":3}"),
            ((long)rows[^1]["seq"]!, (string)rows[^1]["outcome"]!, (string)rows[^1]["detail_json"]!));
    }

    [Fact]
    public async Task Concurrent_phase_writes_for_distinct_operations_remain_isolated()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));
        var handles = Enumerable.Range(0, 12)
            .Select(_ => journal.Begin(JournalTestData.Session(), JournalTestData.Start())!)
            .ToArray();

        await Task.WhenAll(handles.Select((handle, index) => Task.Run(() =>
        {
            journal.RecordPhases(handle,
                [new PhaseAggregate(OperationPhase.Execute, index + 1, index + 10, index + 5)],
                [new PhaseEvent(1, 1, OperationPhase.Connect, 1, "succeeded", null, null)], 0);
            journal.Complete(handle, JournalTestData.End() with { DurationMilliseconds = index + 20 });
        })));

        Assert.Equal(12L, JournalDb.Rows(temp.DatabasePath, "SELECT COUNT(*) AS n FROM operation_phases WHERE phase = 'execute'")[0]["n"]);
        Assert.Equal(12L, JournalDb.Rows(temp.DatabasePath, "SELECT COUNT(*) AS n FROM operation_events")[0]["n"]);
        Assert.Equal(12L, JournalDb.Rows(temp.DatabasePath, "SELECT COUNT(DISTINCT operation_id) AS n FROM operation_phases")[0]["n"]);
    }

    private static void CreateLegacyJournal(string path, int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var seed = connection.CreateCommand();
        seed.CommandText = $"CREATE TABLE legacy_data (value TEXT NOT NULL); INSERT INTO legacy_data VALUES ('legacy-row'); PRAGMA user_version = {version};";
        seed.ExecuteNonQuery();
    }

    private static List<object?> PersistEventDetails(IReadOnlyList<PhaseEvent> events)
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session(), JournalTestData.Start())!;
        journal.RecordPhases(handle, [], events, droppedEvents: 0);
        journal.Complete(handle, JournalTestData.End());
        return JournalDb.Rows(temp.DatabasePath, "SELECT detail_json FROM operation_events ORDER BY seq")
            .Select(row => row["detail_json"])
            .ToList();
    }
}
