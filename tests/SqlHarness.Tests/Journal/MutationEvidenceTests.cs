using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Journal;

public sealed class MutationEvidenceTests
{
    private static readonly string Token = new('A', 64);

    private static void Prepared(IActivityJournal journal, JournalHandle handle, string digest = "digest") =>
        Assert.True(journal.Complete(handle, new OperationEnd("succeeded", 0, null, 1, null, null, null, null, null,
            MutationPreparationDigest: digest, MutationPreparationTokenHash: Hash(Token))));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(-1, null)]
    public void Confirmed_provider_total_is_recorded_for_exact_link(int providerTotal, int? expected)
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session("new"), JournalTestData.Start() with { Operation = "mutation", OperationRef = "ref-1" });
        Assert.NotNull(handle);
        Prepared(journal, handle!);
        journal.RecordMutation(new("req-1", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest", null, null, JournalTestData.T0));
        journal.LinkMutation("req-1", "ref-1", "digest", Token);
        journal.RecordMutationResult("req-1", providerTotal);
        var detail = new JournalReader(temp.DatabasePath, ProcessInfo.Current).Operation(handle!.OperationId)!;
        Assert.Equal(expected, detail.Operation.RecordsAffected);
        Assert.Equal("req-1", detail.MutationRequest?.RequestId);
    }

    [Theory]
    [InlineData("expired", "timeout")]
    [InlineData("outcome_unknown", "outcome_unknown")]
    public void Unconfirmed_outcome_has_lifecycle_but_no_affected_rows(string state, string status)
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session("new"), JournalTestData.Start() with { Operation = "mutation", OperationRef = "ref" })!;
        Prepared(journal, handle);
        journal.RecordMutation(new("req", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest", null, null, JournalTestData.T0));
        journal.LinkMutation("req", "ref", "digest", Token);
        journal.UpdateMutation("req", state, status, null, JournalTestData.T0);
        var detail = new JournalReader(temp.DatabasePath, ProcessInfo.Current).Operation(handle.OperationId)!;
        Assert.Equal(state, detail.MutationRequest!.State);
        Assert.Null(detail.Operation.RecordsAffected);
    }

    [Fact]
    public void Link_requires_matching_preparation_and_consumes_token()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session("new"), JournalTestData.Start() with { Operation = "mutation", OperationRef = "prepared" })!;
        journal.Complete(handle, new OperationEnd("succeeded", 0, null, 1, null, null, null, null, null,
            MutationPreparationDigest: "digest-one", MutationPreparationTokenHash: Hash(Token)));
        journal.LinkMutation("not-recorded", "prepared", "digest-one", Token);
        journal.RecordMutation(new("wrong", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest-two", null, null, JournalTestData.T0));
        journal.LinkMutation("wrong", "prepared", "digest-two", Token);
        Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT operation_id FROM mutation_requests WHERE request_id = 'wrong'").Single()["operation_id"]);
        journal.RecordMutation(new("right", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest-one", null, null, JournalTestData.T0));
        journal.LinkMutation("right", "prepared", "digest-one", Token);
        Assert.Equal(handle.OperationId, JournalDb.Rows(temp.DatabasePath, "SELECT operation_id FROM mutation_requests WHERE request_id = 'right'").Single()["operation_id"]);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM mutation_requests WHERE request_id = 'right'";
            command.ExecuteNonQuery();
        }
        journal.RecordMutation(new("replay", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest-one", null, null, JournalTestData.T0));
        journal.LinkMutation("replay", "prepared", "digest-one", Token);
        Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT operation_id FROM mutation_requests WHERE request_id = 'replay'").Single()["operation_id"]);
    }

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    [Fact]
    public void Migration_from_22_adds_nullable_evidence_without_rewriting_existing_rows()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session("legacy"), JournalTestData.Start() with { Operation = "mutation", OperationRef = "legacy-ref" })!;
        journal.RecordMutation(new("legacy-request", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest", null, null, JournalTestData.T0));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE operation_statements DROP COLUMN grant_requested_kb; ALTER TABLE operation_statements DROP COLUMN grant_granted_kb; ALTER TABLE operation_statements DROP COLUMN grant_max_used_kb; ALTER TABLE operation_statements DROP COLUMN has_spill_warning; ALTER TABLE operations DROP COLUMN mutation_preparation_digest; ALTER TABLE operations DROP COLUMN mutation_preparation_token_hash; ALTER TABLE operations DROP COLUMN records_affected; DROP INDEX ix_mutation_requests_operation; ALTER TABLE mutation_requests DROP COLUMN operation_id; PRAGMA user_version = 22";
            command.ExecuteNonQuery();
        }
        var before = JournalDb.Rows(temp.DatabasePath, "SELECT id, operation, operation_ref, status FROM operations").Single();
        journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        Assert.Equal(before, JournalDb.Rows(temp.DatabasePath, "SELECT id, operation, operation_ref, status FROM operations").Single());
        Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT records_affected FROM operations WHERE id = " + handle.OperationId).Single()["records_affected"]);
        Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT operation_id FROM mutation_requests").Single()["operation_id"]);
    }

    [Fact]
    public void Migration_from_23_keeps_rows_and_does_not_create_preparation_identity()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        journal.Begin(JournalTestData.Session("old"), JournalTestData.Start() with { Operation = "mutation", OperationRef = "legacy" });
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE operation_statements DROP COLUMN grant_requested_kb; ALTER TABLE operation_statements DROP COLUMN grant_granted_kb; ALTER TABLE operation_statements DROP COLUMN grant_max_used_kb; ALTER TABLE operation_statements DROP COLUMN has_spill_warning; ALTER TABLE operations DROP COLUMN mutation_preparation_digest; ALTER TABLE operations DROP COLUMN mutation_preparation_token_hash; PRAGMA user_version = 23";
            command.ExecuteNonQuery();
        }
        var before = JournalDb.Rows(temp.DatabasePath, "SELECT id, operation, operation_ref, status, records_affected FROM operations").Single();
        _ = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        Assert.Equal(before, JournalDb.Rows(temp.DatabasePath, "SELECT id, operation, operation_ref, status, records_affected FROM operations").Single());
        var row = JournalDb.Rows(temp.DatabasePath, "SELECT mutation_preparation_digest, mutation_preparation_token_hash FROM operations").Single();
        Assert.Null(row["mutation_preparation_digest"]);
        Assert.Null(row["mutation_preparation_token_hash"]);
    }

    [Fact]
    public void Future_session_version_neither_writes_nor_projects_version_one_evidence()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        var handle = journal.Begin(JournalTestData.Session("future"), JournalTestData.Start() with { Operation = "mutation", OperationRef = "future-ref" })!;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET dashboard_evidence_version = 2; UPDATE operations SET records_affected = 99";
            command.ExecuteNonQuery();
        }
        Prepared(journal, handle);
        Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT mutation_preparation_token_hash FROM operations").Single()["mutation_preparation_token_hash"]);
        journal.RecordMutation(new("future-request", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest", null, null, JournalTestData.T0));
        journal.LinkMutation("future-request", "future-ref", "digest", Token);
        journal.RecordMutationResult("future-request", 3);
        var detail = new JournalReader(temp.DatabasePath, ProcessInfo.Current).Operation(handle.OperationId)!;
        Assert.Null(detail.MutationRequest);
        Assert.Null(detail.Operation.RecordsAffected);
        Assert.Equal(99L, JournalDb.Rows(temp.DatabasePath, "SELECT records_affected FROM operations").Single()["records_affected"]);
    }

    [Fact]
    public void Old_session_and_missing_reference_are_never_linked()
    {
        using var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        var old = journal.Begin(JournalTestData.Session("old"), JournalTestData.Start() with { Operation = "mutation", OperationRef = "old-ref" })!;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET dashboard_evidence_version = NULL WHERE session_key = 'old'";
            command.ExecuteNonQuery();
        }
        Prepared(journal, old);
        Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT mutation_preparation_token_hash FROM operations").Single()["mutation_preparation_token_hash"]);
        var a = journal.Begin(JournalTestData.Session("new"), JournalTestData.Start() with { Operation = "mutation", OperationRef = "duplicate" })!;
        journal.RecordMutation(new("old-request", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest", null, null, JournalTestData.T0));
        journal.LinkMutation("old-request", "old-ref", "digest", Token);
        journal.RecordMutation(new("ambiguous", "caller", "local", "postgres", "host", "db", "dml", "operator", "digest", null, null, JournalTestData.T0));
        journal.LinkMutation("ambiguous", "missing", "digest", Token);
        journal.RecordMutationResult("old-request", 4);
        journal.RecordMutationResult("ambiguous", 4);
        var reader = new JournalReader(temp.DatabasePath, ProcessInfo.Current);
        Assert.Null(reader.Operation(old.OperationId)!.MutationRequest);
        Assert.Null(reader.Operation(a.OperationId)!.MutationRequest);
        Assert.Null(reader.Operation(old.OperationId)!.Operation.RecordsAffected);
        Assert.Null(reader.Operation(a.OperationId)!.Operation.RecordsAffected);
    }
}