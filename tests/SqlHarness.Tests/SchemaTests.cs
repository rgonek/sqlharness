using System.Data;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class SchemaTests
{
    [Fact]
    public async Task Schema_builds_two_object_report_with_columns_primary_key_index_and_foreign_key()
    {
        var report = await Execute(Reader(2,
            [["dbo", "Child", "TABLE"], ["dbo", "Parent", "TABLE"]],
            columns:
            [
                ["dbo", "Child", "Id", "int", false, true],
                ["dbo", "Child", "ParentId", "int", true, false],
                ["dbo", "Parent", "Id", "int", false, true],
            ],
            indexes:
            [
                ["dbo", "Child", "IX_Child_Parent", false, "ParentId", false, 1, 1, DBNull.Value],
                ["dbo", "Child", "IX_Child_Parent", false, "Id", true, 0, 2, DBNull.Value],
            ],
            foreignKeys:
            [
                ["dbo", "Child", "FK_Child_Parent", "ParentId", "dbo", "Parent", "Id", 1],
            ]));

        Assert.Equal(2, report.Objects.Count);
        var child = report.Objects.Single(x => x.Name == "Child");
        Assert.Collection(child.Columns,
            column => Assert.Equal(new SchemaColumn("Id", "int", false, true), column),
            column => Assert.Equal(new SchemaColumn("ParentId", "int", true, false), column));
        var index = Assert.Single(child.Indexes);
        Assert.Equal(["ParentId"], index.Keys);
        Assert.Equal(["Id"], index.Includes);
        Assert.Equal(new SchemaForeignKey("FK_Child_Parent", "ParentId", "dbo.Parent", "Id"), Assert.Single(child.ForeignKeys));
    }

    [Fact]
    public async Task Default_cap_uses_exact_total_for_ten_thousand_objects()
    {
        var rows = Enumerable.Range(1, 50).Select(i => new object?[] { "dbo", $"T{i:000}", "TABLE" }).ToArray();
        var report = await Execute(Reader(10_000, rows), max: 50);
        Assert.Equal(50, report.Objects.Count);
        Assert.Equal(9_950, report.OmittedObjects);
    }

    [Fact]
    public async Task Maximum_cap_uses_exact_total_beyond_old_501_boundary()
    {
        var rows = Enumerable.Range(1, 500).Select(i => new object?[] { "dbo", $"T{i:000}", "TABLE" }).ToArray();
        var report = await Execute(Reader(20_000, rows), max: 500);
        Assert.Equal(500, report.Objects.Count);
        Assert.Equal(19_500, report.OmittedObjects);
    }

    [Fact]
    public async Task Rowwise_index_metadata_preserves_order_and_identifiers_exactly()
    {
        var weirdObject = "Zamówienia,]Łódź";
        var reader = Reader(1, [["dbo", weirdObject, "TABLE"]], indexes:
        [
            ["dbo", weirdObject, "IX,]Ż", false, "include,]ą", true, 0, 2, DBNull.Value],
            ["dbo", weirdObject, "IX,]Ż", false, "key,]β", false, 2, 0, DBNull.Value],
            ["dbo", weirdObject, "IX,]Ż", false, "key,]α", false, 1, 0, DBNull.Value],
            ["dbo", weirdObject, "IX,]Ż", false, "include,]ć", true, 0, 1, DBNull.Value],
        ]);
        var report = await Execute(reader);
        var index = Assert.Single(Assert.Single(report.Objects).Indexes);
        Assert.Equal("IX,]Ż", index.Name);
        Assert.Equal(["key,]α", "key,]β"], index.Keys);
        Assert.Equal(["include,]ć", "include,]ą"], index.Includes);
        Assert.Equal(weirdObject, report.Objects[0].Name);
    }

    [Fact]
    public async Task Long_include_lists_are_not_aggregated_or_truncated()
    {
        var includes = Enumerable.Range(1, 1_000)
            .Select(i => new object?[] { "dbo", "Big", "IX_Big", false, $"Include_{i:0000}_ą,]", true, 0, i, DBNull.Value })
            .ToArray();
        var report = await Execute(Reader(1, [["dbo", "Big", "TABLE"]], indexes: includes));
        var actual = Assert.Single(Assert.Single(report.Objects).Indexes).Includes;
        Assert.Equal(1_000, actual.Count);
        Assert.Equal("Include_0001_ą,]", actual[0]);
        Assert.Equal("Include_1000_ą,]", actual[^1]);
    }

    [Fact]
    public async Task Batch_is_fixed_read_only_rowwise_and_all_inputs_are_bound()
    {
        var session = new FakeSession(Reader(0, []));
        var gain = new FakeGain();
        var outcome = await new SqlHarnessModule(session, gain, Profiles)
            .ExecuteAsync(new SqlHarnessSchemaOperation(Target(), "%secret%", 30, 75));
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var command = Assert.Single(session.Commands);
        Assert.DoesNotContain("STRING_AGG", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WITHIN GROUP", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QUOTENAME", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%secret%", command.Sql, StringComparison.Ordinal);
        Assert.Contains("COUNT_BIG", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TOP (@maxObjects)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(command.Parameters,
            p => { Assert.Equal("@filter", p.Name); Assert.Equal(SqlDbType.NVarChar, p.Type); Assert.Equal("%secret%", p.Value); },
            p => { Assert.Equal("@maxObjects", p.Name); Assert.Equal(SqlDbType.Int, p.Type); Assert.Equal(75, p.Value); });
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt).CompleteAsync(new(20, 2));
        Assert.Equal("schema", Assert.Single(gain.Records).Command);
    }

    [Fact]
    public async Task Schema_preserves_target_mismatch_mapping_without_executing_batch()
    {
        var session = new FakeSession(Reader(0, [])) { ConnectFailure = new SqlTargetMismatchException("mismatch") };
        var outcome = await new SqlHarnessModule(session, new FakeGain(), Profiles)
            .ExecuteAsync(new SqlHarnessSchemaOperation(Target(), null, 30, 50));
        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Empty(session.Commands);
    }

    private static async Task<SqlHarnessSchemaReport> Execute(FakeReader reader, int max = 50)
    {
        var outcome = await new SqlHarnessModule(new FakeSession(reader), new FakeGain(), Profiles)
            .ExecuteAsync(new SqlHarnessSchemaOperation(Target(), null, 30, max));
        return Assert.IsType<SqlHarnessSchemaReport>(outcome.Report);
    }

    private static FakeReader Reader(
        long total,
        object?[][] objects,
        object?[][]? columns = null,
        object?[][]? indexes = null,
        object?[][]? foreignKeys = null) => new(
        Set(["TotalObjects"], [total]),
        Set(["SchemaName", "ObjectName", "Kind"], objects),
        Set(["SchemaName", "ObjectName", "ColumnName", "TypeName", "Nullable", "InPrimaryKey"], columns ?? []),
        Set(["SchemaName", "ObjectName", "IndexName", "IsUnique", "ColumnName", "IsIncluded", "KeyOrdinal", "IndexColumnOrdinal", "Filter"], indexes ?? []),
        Set(["SchemaName", "ObjectName", "ForeignKeyName", "ColumnName", "ReferencedSchema", "ReferencedObject", "ReferencedColumn", "ConstraintOrdinal"], foreignKeys ?? []));
    private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];
    private static SqlTargetRequest Target() => new("test", new Dictionary<string, string>());
    private static IReadOnlyDictionary<string, TargetProfile> Profiles() => new Dictionary<string, TargetProfile> { ["test"] = new("server", "db", new Dictionary<string, string>(), "integrated") };
    private sealed class FakeGain : IGainStore
    {
        public List<GainRecord> Records { get; } = [];
        public void Append(GainRecord record) => Records.Add(record);
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }
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
        private string[] Names => sets[_set][0].Cast<string>().ToArray(); private object?[][] Rows => sets[_set].Skip(1).ToArray();
        public int FieldCount => Names.Length; public int RecordsAffected => -1; public string GetName(int ordinal) => Names[ordinal]; public Type GetFieldType(int ordinal) => typeof(object); public bool GetAllowNull(int ordinal) => true; public object GetValue(int ordinal) => Rows[_row - 1][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) { if (_row < Rows.Length) { _row++; return Task.FromResult(true); } return Task.FromResult(false); }
        public Task<bool> NextResultAsync(CancellationToken ct) { if (++_set < sets.Length) { _row = 0; return Task.FromResult(true); } return Task.FromResult(false); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
