using System.Data;

using Microsoft.Data.SqlClient.Server;
using SqlHarness.Core;
using SqlHarness.Core.Dialect;

namespace SqlHarness.Tests;

public sealed class TableParameterMaterializerTests
{
    [Fact]
    public async Task Unqualified_type_resolves_schema()
    {
        var session = FakeSession.Catalog("dbo", "IdList", [new FakeColumn("Id", "int", 4, 10, 0, false)]);

        var shape = await SqlServerTableTypes.ResolveAsync(session, "IdList", CancellationToken.None);

        Assert.Equal("[dbo].[IdList]", shape.QualifiedName);
        Assert.Equal("Id", Assert.Single(shape.Columns).Name);
        Assert.Equal(SqlDbType.Int, shape.Columns[0].Type);
    }

    [Fact]
    public async Task Empty_tvp_binds_zero_rows()
    {
        var session = FakeSession.Catalog("dbo", "IdList", [new FakeColumn("Id", "int", 4, 10, 0, false)]);
        var input = new SqlHarnessParameterInput("Ids", "dbo.IdList", null)
        {
            Kind = "tvp",
            Rows = [],
        };

        var materialized = await TableParameterMaterializer.MaterializeAsync(session, [input], SqlDialects.For(SqlEngine.SqlServer), CancellationToken.None);

        var parameter = Assert.Single(materialized.Parameters);
        Assert.Equal(SqlDbType.Structured, parameter.Type);
        Assert.Equal("[dbo].[IdList]", parameter.TableTypeName);
        Assert.Empty(parameter.Rows!);

        using var command = new Microsoft.Data.SqlClient.SqlCommand();
        SqlClientSession.BindCommand(command, new SqlExecutionCommand("SELECT * FROM @Ids", [parameter], 30));
        Assert.Equal("[dbo].[IdList]", command.Parameters["@Ids"].TypeName);
        Assert.Equal(SqlDbType.Structured, command.Parameters["@Ids"].SqlDbType);
    }

    [Fact]
    public async Task Inline_rows_convert_using_catalog_column_types_and_order()
    {
        var columns = new[]
        {
            new FakeColumn("Id", "int", 4, 10, 0, false),
            new FakeColumn("Name", "nvarchar", 100, 0, 0, true),
            new FakeColumn("Amount", "decimal", 9, 18, 4, false),
            new FakeColumn("Created", "datetime2", 8, 27, 7, false),
            new FakeColumn("Enabled", "bit", 1, 1, 0, false),
        };
        var session = FakeSession.Catalog("dbo", "TypedRows", columns);
        var input = new SqlHarnessParameterInput("Rows", "dbo.TypedRows", null)
        {
            Kind = "tvp",
            Rows = [["42", "widget", "12.3400", "2026-01-02T03:04:05.1234567", "true"]],
        };

        var materialized = await TableParameterMaterializer.MaterializeAsync(session, [input], SqlDialects.For(SqlEngine.SqlServer), CancellationToken.None);
        var values = Assert.Single(Assert.Single(materialized.Parameters).Rows!);

        Assert.Equal(42, values.GetValue(0));
        Assert.Equal("widget", values.GetValue(1));
        Assert.Equal(12.3400m, values.GetValue(2));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1_234_567), values.GetValue(3));
        Assert.Equal(true, values.GetValue(4));
    }

    [Fact]
    public async Task Inline_row_column_count_mismatch_names_type_and_parameter()
    {
        var session = FakeSession.Catalog("dbo", "IdList", [new FakeColumn("Id", "int", 4, 10, 0, false)]);
        var input = new SqlHarnessParameterInput("Ids", "dbo.IdList", null)
        {
            Kind = "tvp",
            Rows = [["1", "2"]],
        };

        var exception = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            TableParameterMaterializer.MaterializeAsync(session, [input], SqlDialects.For(SqlEngine.SqlServer), CancellationToken.None));

        Assert.Equal("rows for @Ids do not match dbo.IdList: expected 1 values, found 2", exception.Message);
    }

    [Fact]
    public async Task Rows_from_rejects_source_column_type_mismatch_without_conversion()
    {
        var session = FakeSession.WithSource(
            "dbo", "IdList",
            [new FakeColumn("Id", "int", 4, 10, 0, false)],
            "nvarchar", ["1"]);
        var input = new SqlHarnessParameterInput("Ids", "dbo.IdList", null)
        {
            Kind = "tvp",
            SourceSql = "SELECT TextValue FROM dbo.Source",
        };

        var exception = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            TableParameterMaterializer.MaterializeAsync(session, [input], SqlDialects.For(SqlEngine.SqlServer), CancellationToken.None));

        Assert.Contains("rows for @Ids do not match dbo.IdList", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expected 'int'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_from_enforces_combined_row_limit_during_materialization()
    {
        var values = Enumerable.Range(0, 100_001).Cast<object?>().ToArray();
        var session = FakeSession.WithSource(
            "dbo", "IdList",
            [new FakeColumn("Id", "int", 4, 10, 0, false)],
            "int", values);
        var input = new SqlHarnessParameterInput("Ids", "dbo.IdList", null)
        {
            Kind = "tvp",
            SourceSql = "SELECT Id FROM dbo.Source",
        };

        var exception = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            TableParameterMaterializer.MaterializeAsync(session, [input], SqlDialects.For(SqlEngine.SqlServer), CancellationToken.None));

        Assert.Equal("SQL parameter '@Ids' exceeds the 100000 combined rows/values limit.", exception.Message);
    }

    private sealed record FakeColumn(string Name, string TypeName, int Size, byte Precision, byte Scale, bool Nullable);

    private sealed class FakeSession(string qualifiedName, FakeColumn[] catalogColumns, string? sourceType = null, object?[]? sourceValues = null) : ISqlSession
    {
        public IReadOnlyList<SessionMessage> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;
        public static FakeSession Catalog(string schema, string type, FakeColumn[] columns) => new($"[{schema}].[{type}]", columns);
        public static FakeSession WithSource(string schema, string type, FakeColumn[] columns, string sourceType, object?[] sourceValues) =>
            new($"[{schema}].[{type}]", columns, sourceType, sourceValues);
        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct) => Task.FromResult<ISqlReader>(
            command.Sql.Contains("sys.table_types", StringComparison.Ordinal)
                ? new FakeReader(qualifiedName, catalogColumns)
                : new FakeSourceReader(sourceType!, sourceValues!));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSourceReader(string typeName, object?[] values) : ISqlReader
    {
        private int index = -1;
        public int FieldCount => 1;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => "SourceValue";
        public Type GetFieldType(int ordinal) => values.FirstOrDefault()?.GetType() ?? typeof(string);
        public string GetSqlTypeName(int ordinal) => typeName;
        public SqlReaderColumnMetadata GetColumnMetadata(int ordinal) => new(typeName, null, null, null);
        public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal) => values[index]!;
        public Task<bool> ReadAsync(CancellationToken ct) { index++; return Task.FromResult(index < values.Length); }
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader(string qualifiedName, FakeColumn[] columns) : ISqlReader
    {
        private int index = -1;
        public int FieldCount => 7;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => ordinal switch { 0 => "QualifiedName", 1 => "ColumnName", _ => ordinal.ToString() };
        public Type GetFieldType(int ordinal) => ordinal switch { 0 or 1 or 2 => typeof(string), 3 or 4 or 5 => typeof(int), 6 => typeof(bool), _ => typeof(object) };
        public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal)
        {
            var column = columns[index];
            return ordinal switch
            {
                0 => qualifiedName, 1 => column.Name, 2 => column.TypeName,
                3 => column.Size, 4 => (int)column.Precision, 5 => (int)column.Scale, 6 => column.Nullable,
                _ => throw new ArgumentOutOfRangeException(nameof(ordinal)),
            };
        }
        public Task<bool> ReadAsync(CancellationToken ct) { index++; return Task.FromResult(index < columns.Length); }
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
