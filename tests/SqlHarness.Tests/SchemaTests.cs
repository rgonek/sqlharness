using System.Data;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class SchemaTests
{
    [Fact]
    public async Task Schema_builds_deterministic_report_binds_filter_and_caps_objects()
    {
        var session = new FakeSession(new FakeReader(
            Set(["SchemaName", "ObjectName", "Kind"], ["dbo", "Child", "TABLE"], ["dbo", "Parent", "TABLE"], ["dbo", "Spare", "VIEW"]),
            Set(["SchemaName", "ObjectName", "ColumnName", "TypeName", "Nullable", "InPrimaryKey"], ["dbo", "Child", "Id", "int", false, true], ["dbo", "Child", "ParentId", "int", true, false], ["dbo", "Parent", "Id", "int", false, true]),
            Set(["SchemaName", "ObjectName", "IndexName", "IsUnique", "Keys", "Includes", "Filter"], ["dbo", "Child", "IX_Child_Parent", false, "ParentId", "Id", DBNull.Value]),
            Set(["SchemaName", "ObjectName", "ForeignKeyName", "Columns", "ReferencedTable", "ReferencedColumns"], ["dbo", "Child", "FK_Child_Parent", "ParentId", "dbo.Parent", "Id"])));
        var gain = new FakeGain();
        var module = new SqlHarnessModule(session, gain, Profiles);

        var outcome = await module.ExecuteAsync(new SqlHarnessSchemaOperation(Target(), "%ar%", 30, 2));

        var report = Assert.IsType<SqlHarnessSchemaReport>(outcome.Report);
        Assert.Equal(2, report.Objects.Count);
        Assert.Equal(1, report.OmittedObjects);
        Assert.Equal(["Child", "Parent"], report.Objects.Select(x => x.Name));
        Assert.True(report.Objects[0].Columns[0].InPrimaryKey);
        Assert.Equal(["ParentId"], report.Objects[0].Indexes[0].Keys);
        Assert.Equal(["Id"], report.Objects[0].Indexes[0].Includes);
        Assert.Equal("dbo.Parent", report.Objects[0].ForeignKeys[0].ReferencedTable);
        var command = Assert.Single(session.Commands);
        var filter = Assert.Single(command.Parameters);
        Assert.Equal("@filter", filter.Name);
        Assert.Equal(SqlDbType.NVarChar, filter.Type);
        Assert.Equal("%ar%", filter.Value);
        Assert.DoesNotContain("%ar%", command.Sql, StringComparison.Ordinal);
        Assert.Contains("sys.foreign_key_columns", command.Sql, StringComparison.OrdinalIgnoreCase);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new(20, 2));
        Assert.Equal("schema", Assert.Single(gain.Records).Command);
    }

    [Fact]
    public async Task Schema_preserves_target_mismatch_mapping_without_executing_batch()
    {
        var session = new FakeSession(new FakeReader(Set(["x"]))) { ConnectFailure = new SqlTargetMismatchException("mismatch") };
        var outcome = await new SqlHarnessModule(session, new FakeGain(), Profiles)
            .ExecuteAsync(new SqlHarnessSchemaOperation(Target(), null, 30, 50));
        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Empty(session.Commands);
    }

    private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];
    private static SqlTargetRequest Target() => new("test", new Dictionary<string, string>());
    private static IReadOnlyDictionary<string, TargetProfile> Profiles() => new Dictionary<string, TargetProfile> { ["test"] = new("server", "db", new Dictionary<string, string>(), "integrated") };

    private sealed class FakeGain : IGainStore { public List<GainRecord> Records { get; } = []; public void Append(GainRecord r) => Records.Add(r); public SqlHarnessGainReport Aggregate() => throw new NotSupportedException(); }
    private sealed class FakeSession(FakeReader reader) : ISqlSessionFactory, ISqlSession
    {
        public Exception? ConnectFailure { get; init; }
        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = new("server", "db", "server", "db", "profile");
        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct) => ConnectFailure is null ? Task.FromResult<ISqlSession>(this) : Task.FromException<ISqlSession>(ConnectFailure);
        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) { Commands.Add(command); return Task.FromResult<ISqlReader>(reader); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeReader(params object?[][][] sets) : ISqlReader
    {
        private int _set; private int _row;
        private string[] Names => sets[_set][0].Cast<string>().ToArray();
        private object?[][] Rows => sets[_set].Skip(1).ToArray();
        public int FieldCount => Names.Length; public int RecordsAffected => -1;
        public string GetName(int ordinal) => Names[ordinal]; public Type GetFieldType(int ordinal) => typeof(object); public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal) => Rows[_row - 1][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) { if (_row < Rows.Length) { _row++; return Task.FromResult(true); } return Task.FromResult(false); }
        public Task<bool> NextResultAsync(CancellationToken ct) { if (++_set < sets.Length) { _row = 0; return Task.FromResult(true); } return Task.FromResult(false); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}