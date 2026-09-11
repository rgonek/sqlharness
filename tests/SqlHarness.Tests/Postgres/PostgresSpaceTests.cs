using System.Data;

using SqlHarness.Core;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresSpaceTests
{
    [Fact]
    public void Sql_uses_pg_catalog_sizes_and_not_sys_database_files()
    {
        var sql = PostgresSpace.Sql;

        Assert.Contains("pg_database_size", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_total_relation_size", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_relation_size", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_class", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_namespace", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_index", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reltuples", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(SELECT setting FROM pg_settings WHERE name = 'data_directory')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("current_setting('data_directory')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("current_setting(\"data_directory\")", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'DATA'", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT @top", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sys.database_files", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sys.dm_db_partition_stats", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FILEPROPERTY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VACUUM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHRINK", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP (", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_excludes_system_namespaces_and_matches_relname_exactly()
    {
        var sql = PostgresSpace.Sql;

        Assert.Contains("pg_catalog", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema", sql, StringComparison.Ordinal);
        Assert.Contains("pg_toast", sql, StringComparison.Ordinal);
        Assert.Contains("pg_temp", sql, StringComparison.Ordinal);
        Assert.Contains("c.relname = @objectName", sql, StringComparison.Ordinal);
        Assert.Contains("n.nspname = @objectSchema", sql, StringComparison.Ordinal);
        Assert.Contains("am.amname", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_builds_files_allocation_tables_and_indexes()
    {
        var reader = Reader(
            matchCount: 1,
            files: [["appdb", "DATA", "/var/lib/postgresql/data", 512m, 512m, 0m]],
            allocation: [400m, 250m, 180m],
            tables: [["public", "Contracts", 42L, 100m, 100m, 70m]],
            indexes:
            [
                ["public", "Contracts", "Contracts_pkey", "btree", 12m, 12m, 12m, null],
                ["public", "Contracts", "IX_Contracts_Date", "btree", 8m, 8m, 8m, null],
            ]);

        var result = await PostgresSpace.ReadAsync(reader, objectRequested: true, CancellationToken.None);

        var file = Assert.Single(result.Files);
        Assert.Equal("appdb", file.LogicalName);
        Assert.Equal("DATA", file.Type);
        Assert.Equal("/var/lib/postgresql/data", file.PhysicalName);
        Assert.Equal(512m, file.SizeMb);
        Assert.Equal(400m, result.Allocation.ReservedMb);
        Assert.Equal(250m, result.Allocation.UsedMb);
        Assert.Equal(180m, result.Allocation.DataMb);
        var table = Assert.Single(result.Tables);
        Assert.Equal("public", table.Schema);
        Assert.Equal("Contracts", table.Name);
        Assert.Equal(42L, table.Rows);
        Assert.Collection(result.Indexes,
            index =>
            {
                Assert.Equal("Contracts_pkey", index.Index);
                Assert.Equal("btree", index.Type);
                Assert.Null(index.Compression);
            },
            index =>
            {
                Assert.Equal("IX_Contracts_Date", index.Index);
                Assert.Equal("btree", index.Type);
                Assert.Null(index.Compression);
            });
        Assert.True(result.Raw.Bytes > 0);
    }

    [Fact]
    public async Task ReadAsync_null_physical_name_and_empty_indexes_without_object()
    {
        var reader = Reader(
            matchCount: 0,
            files: [["appdb", "DATA", null, 100m, 100m, 0m]],
            allocation: [100m, 40m, 30m],
            tables: [["public", "Contracts", 10L, 20m, 20m, 15m]],
            indexes: []);

        var result = await PostgresSpace.ReadAsync(reader, objectRequested: false, CancellationToken.None);

        Assert.Null(Assert.Single(result.Files).PhysicalName);
        Assert.Empty(result.Indexes);
        Assert.Equal(100m, result.Allocation.ReservedMb);
    }

    [Fact]
    public async Task ReadAsync_zero_matches_in_object_mode_is_safety()
    {
        var reader = Reader(
            matchCount: 0,
            files: [["appdb", "DATA", null, 100m, 100m, 0m]],
            allocation: [100m, 40m, 30m],
            tables: [],
            indexes: []);

        var ex = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            PostgresSpace.ReadAsync(reader, objectRequested: true, CancellationToken.None));

        Assert.Equal(SpaceQuery.MissingOrAmbiguousMessage, ex.Message);
    }

    [Fact]
    public async Task Module_postgres_profile_executes_PostgresSpace_Sql()
    {
        var session = new FakeSession(Reader(
            matchCount: 0,
            files: [["appdb", "DATA", "/data", 200m, 200m, 0m]],
            allocation: [200m, 80m, 60m],
            tables: [["public", "Contracts", 42L, 50m, 50m, 30m]],
            indexes: []));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessSpaceReport>(outcome.Report);
        Assert.Equal("DATA", Assert.Single(report.Files).Type);
        Assert.Equal("Contracts", Assert.Single(report.Tables).Name);
        Assert.Empty(report.Indexes);
        var command = Assert.Single(session.Commands);
        Assert.Equal(PostgresSpace.Sql, command.Sql);
        Assert.DoesNotContain("sys.database_files", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(command.Parameters,
            p => { Assert.Equal("@top", p.Name); Assert.Equal(SqlDbType.Int, p.Type); Assert.Equal(25, p.Value); },
            p => { Assert.Equal("@objectSchema", p.Name); Assert.Equal(DBNull.Value, p.Value); },
            p => { Assert.Equal("@objectName", p.Name); Assert.Equal(DBNull.Value, p.Value); });
    }

    [Fact]
    public async Task Object_mode_binds_schema_and_name_and_returns_indexes()
    {
        var session = new FakeSession(Reader(
            matchCount: 1,
            files: [["appdb", "DATA", null, 200m, 200m, 0m]],
            allocation: [200m, 80m, 60m],
            tables: [["public", "Contracts", 42L, 50m, 50m, 30m]],
            indexes: [["public", "Contracts", "IX_Contracts_Date", "btree", 12m, 12m, 12m, null]]));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 40, "public.Contracts", 20));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessSpaceReport>(outcome.Report);
        var index = Assert.Single(report.Indexes);
        Assert.Equal("IX_Contracts_Date", index.Index);
        Assert.Equal("btree", index.Type);
        Assert.Null(index.Compression);
        var command = Assert.Single(session.Commands);
        Assert.Equal(PostgresSpace.Sql, command.Sql);
        Assert.Equal(20, command.TimeoutSeconds);
        Assert.Contains(command.Parameters, p => p.Name == "@top" && Equals(p.Value, 40));
        Assert.Contains(command.Parameters, p => p.Name == "@objectSchema" && Equals(p.Value, "public"));
        Assert.Contains(command.Parameters, p => p.Name == "@objectName" && Equals(p.Value, "Contracts"));
        Assert.DoesNotContain(command.Parameters, p => Equals(p.Value, "contracts"));
    }

    [Fact]
    public async Task Object_mode_missing_uses_MissingOrAmbiguousMessage()
    {
        var session = new FakeSession(Reader(
            matchCount: 0,
            files: [["appdb", "DATA", null, 100m, 100m, 0m]],
            allocation: [100m, 40m, 30m],
            tables: [],
            indexes: []));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, "MissingTable", 30));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(SpaceQuery.MissingOrAmbiguousMessage, outcome.SafeError);
        Assert.Equal(PostgresSpace.Sql, Assert.Single(session.Commands).Sql);
    }

    [Fact]
    public async Task Object_mode_ambiguous_exits_safety()
    {
        var session = new FakeSession(Reader(
            matchCount: 2,
            files: [["appdb", "DATA", null, 100m, 100m, 0m]],
            allocation: [100m, 40m, 30m],
            tables: [],
            indexes: []));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, "Contracts", 30));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(SpaceQuery.MissingOrAmbiguousMessage, outcome.SafeError);
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
        long matchCount,
        object?[][] files,
        object?[] allocation,
        object?[][] tables,
        object?[][] indexes) => new(
        Set(["MatchCount"], [matchCount]),
        Set(["LogicalName", "Type", "PhysicalName", "SizeMb", "UsedMb", "FreeMb"], files),
        Set(["ReservedMb", "UsedMb", "DataMb"], allocation),
        Set(["SchemaName", "ObjectName", "Rows", "ReservedMb", "UsedMb", "DataMb"], tables),
        Set(["SchemaName", "TableName", "IndexName", "Type", "ReservedMb", "UsedMb", "DataMb", "Compression"], indexes));

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
