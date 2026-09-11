using System.Data;

using SqlHarness.Core;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresSchemaTests
{
    [Fact]
    public void Sql_uses_pg_catalog_and_not_sys_columns()
    {
        var sql = PostgresSchema.Sql;

        Assert.Contains("pg_class", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_attribute", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_index", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_constraint", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_namespace", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sys.columns", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sys.objects", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COUNT_BIG", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP (", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIKE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ILIKE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_excludes_system_namespaces_and_matches_relname_exactly()
    {
        var sql = PostgresSchema.Sql;

        Assert.Contains("pg_catalog", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema", sql, StringComparison.Ordinal);
        Assert.Contains("pg_toast", sql, StringComparison.Ordinal);
        Assert.Contains("pg_temp", sql, StringComparison.Ordinal);
        Assert.Contains("c.relname = @objectName", sql, StringComparison.Ordinal);
        Assert.Contains("n.nspname = @objectSchema", sql, StringComparison.Ordinal);
        Assert.Contains("c.relname LIKE @filter", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @maxObjects", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_builds_object_with_columns_pk_index_include_filter_and_fk()
    {
        var reader = Reader(
            1,
            [["public", "Child", "TABLE"]],
            columns:
            [
                ["public", "Child", "Id", "integer", false, true],
                ["public", "Child", "ParentId", "integer", true, false],
            ],
            indexes:
            [
                ["public", "Child", "IX_Child_Parent", false, "ParentId", false, 1, 0, "(ParentId > 0)"],
                ["public", "Child", "IX_Child_Parent", false, "Id", true, 0, 1, "(ParentId > 0)"],
            ],
            foreignKeys:
            [
                ["public", "Child", "FK_Child_Parent", "ParentId", "public", "Parent", "Id", 1],
            ]);

        var result = await PostgresSchema.ReadAsync(reader, CancellationToken.None);

        var child = Assert.Single(result.Objects);
        Assert.Equal("public", child.Schema);
        Assert.Equal("Child", child.Name);
        Assert.Equal("TABLE", child.Kind);
        Assert.Collection(child.Columns,
            column => Assert.Equal(new SchemaColumn("Id", "integer", false, true), column),
            column => Assert.Equal(new SchemaColumn("ParentId", "integer", true, false), column));
        var index = Assert.Single(child.Indexes);
        Assert.Equal("IX_Child_Parent", index.Name);
        Assert.Equal(["ParentId"], index.Keys);
        Assert.Equal(["Id"], index.Includes);
        Assert.Equal("(ParentId > 0)", index.Filter);
        Assert.Equal(
            new SchemaForeignKey("FK_Child_Parent", "ParentId", "public.Parent", "Id"),
            Assert.Single(child.ForeignKeys));
        Assert.Equal(0, result.Omitted);
    }

    [Fact]
    public async Task Module_postgres_profile_executes_PostgresSchema_Sql()
    {
        var session = new FakeSession(Reader(0, []));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSchemaOperation(Target(), "%secret%", 30, 75));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var command = Assert.Single(session.Commands);
        Assert.Equal(PostgresSchema.Sql, command.Sql);
        Assert.DoesNotContain("%secret%", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.columns", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(command.Parameters,
            p => { Assert.Equal("@filter", p.Name); Assert.Equal(SqlDbType.NVarChar, p.Type); Assert.Equal("%secret%", p.Value); },
            p => { Assert.Equal("@maxObjects", p.Name); Assert.Equal(SqlDbType.Int, p.Type); Assert.Equal(75, p.Value); },
            p => { Assert.Equal("@objectSchema", p.Name); Assert.Equal(DBNull.Value, p.Value); },
            p => { Assert.Equal("@objectName", p.Name); Assert.Equal(DBNull.Value, p.Value); });
    }

    [Fact]
    public async Task Object_mode_with_zero_rows_uses_MissingOrAmbiguousMessage()
    {
        var session = new FakeSession(Reader(0, []));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSchemaOperation(Target(), null, 30, 50, "MissingTable"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(SchemaReader.MissingOrAmbiguousMessage, outcome.SafeError);
        Assert.Equal(PostgresSchema.Sql, Assert.Single(session.Commands).Sql);
    }

    [Fact]
    public async Task Object_mode_binds_schema_and_name_without_lowercasing()
    {
        var session = new FakeSession(Reader(1, [["public", "Contracts", "TABLE"]]));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSchemaOperation(Target(), null, 30, 50, "public.Contracts"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessSchemaReport>(outcome.Report);
        Assert.Equal("Contracts", Assert.Single(report.Objects).Name);
        var command = Assert.Single(session.Commands);
        Assert.Equal(PostgresSchema.Sql, command.Sql);
        Assert.Contains(command.Parameters, p => p.Name == "@objectSchema" && Equals(p.Value, "public"));
        Assert.Contains(command.Parameters, p => p.Name == "@objectName" && Equals(p.Value, "Contracts"));
        Assert.DoesNotContain(command.Parameters, p => Equals(p.Value, "contracts"));
    }

    [Fact]
    public async Task Object_mode_ambiguous_unqualified_name_exits_safety()
    {
        var session = new FakeSession(Reader(2,
        [
            ["public", "Contracts", "TABLE"],
            ["sales", "Contracts", "TABLE"],
        ]));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSchemaOperation(Target(), null, 30, 50, "Contracts"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(SchemaReader.MissingOrAmbiguousMessage, outcome.SafeError);
    }

    [Fact]
    public async Task Filter_mode_reports_omitted_objects()
    {
        var rows = Enumerable.Range(1, 2).Select(i => new object?[] { "public", $"T{i}", "TABLE" }).ToArray();
        var session = new FakeSession(Reader(5, rows));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSchemaOperation(Target(), "%", 30, 2));

        var report = Assert.IsType<SqlHarnessSchemaReport>(outcome.Report);
        Assert.Equal(2, report.Objects.Count);
        Assert.Equal(3, report.OmittedObjects);
        Assert.Equal(PostgresSchema.Sql, Assert.Single(session.Commands).Sql);
    }

    private static SqlHarnessModule Module(FakeSession session) =>
        new(session, new FakeGain(), Profiles);

    private static SqlTargetRequest Target() =>
        new("local-pg", new Dictionary<string, string>());

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["local-pg"] = new(
                "localhost,5432",
                "appdb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
                TrustServerCertificate: true,
                Engine: "postgres"),
        };

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

    private static object?[][] Set(string[] names, params object?[][] rows) =>
        [[.. names], .. rows];

    private sealed class FakeGain : IGainStore
    {
        public void Append(GainRecord record) { }
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSession(FakeReader reader) : ISqlSessionFactory, ISqlSession
    {
        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("localhost", "appdb", "localhost", "appdb", "profile", Engine: "postgres");

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct) =>
            Task.FromResult<ISqlSession>(this);

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult<ISqlReader>(reader);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader(params object?[][][] sets) : ISqlReader
    {
        private int _set;
        private int _row;

        private string[] Names => sets[_set][0].Cast<string>().ToArray();
        private object?[][] Rows => sets[_set].Skip(1).ToArray();

        public int FieldCount => Names.Length;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => Names[ordinal];
        public Type GetFieldType(int ordinal) => typeof(object);
        public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal) => Rows[_row - 1][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            if (_row < Rows.Length)
            {
                _row++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (++_set < sets.Length)
            {
                _row = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
