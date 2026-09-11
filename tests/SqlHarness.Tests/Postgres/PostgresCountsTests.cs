using System.Data;

using SqlHarness.Core;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresCountsTests
{
    [Fact]
    public void CatalogSql_uses_pg_class_reltuples_and_excludes_system_namespaces()
    {
        var sql = PostgresCounts.CatalogSql;

        Assert.Contains("pg_class", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reltuples", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_namespace", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_catalog", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema", sql, StringComparison.Ordinal);
        Assert.Contains("pg_toast", sql, StringComparison.Ordinal);
        Assert.Contains("pg_temp", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ILIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sys.dm_db_partition_stats", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPENJSON", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sys.tables", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildExactSql_quotes_catalog_names_with_count_star()
    {
        var objects = new[]
        {
            new ResolvedCountObject("public.Contracts", "public", "Contracts", 1, 10),
            new ResolvedCountObject("audit.Runs", "audit", "Runs", 2, 20),
        };

        var sql = PostgresCounts.BuildExactSql(objects);

        Assert.Contains("COUNT(*)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COUNT_BIG", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"public\".\"Contracts\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"audit\".\"Runs\"", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(0 AS int)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST(1 AS int)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contracts", PostgresCounts.CatalogSql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExactSql_escapes_embedded_double_quotes()
    {
        var objects = new[]
        {
            new ResolvedCountObject("public.na\"me", "public", "na\"me", 1, 1),
        };

        var sql = PostgresCounts.BuildExactSql(objects);

        Assert.Contains("\"public\".\"na\"\"me\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadCatalog_like_mode_reports_omitted()
    {
        var reader = CatalogReader(
            5,
            [null, "public", "Big", 1, 1000L],
            [null, "public", "Small", 2, 1L]);

        var resolved = await CountsQuery.ReadCatalogAsync(reader, [], CancellationToken.None);

        Assert.Equal(3, resolved.Omitted);
        Assert.Equal(
            [
                new ResolvedCountObject("", "public", "Big", 1, 1000),
                new ResolvedCountObject("", "public", "Small", 2, 1),
            ],
            resolved.Objects);
    }

    [Fact]
    public async Task ReadCatalog_ambiguous_unqualified_name_throws_safety()
    {
        var reader = CatalogReader(
            0,
            ["Contracts", "public", "Contracts", 1, 10L],
            ["Contracts", "sales", "Contracts", 2, 20L]);

        var error = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            CountsQuery.ReadCatalogAsync(reader, ["Contracts"], CancellationToken.None));

        Assert.Equal("Requested table 'Contracts' was not found or was ambiguous.", error.Message);
    }

    [Fact]
    public async Task ReadCatalog_missing_table_throws_safety()
    {
        var reader = CatalogReader(0);

        var error = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            CountsQuery.ReadCatalogAsync(reader, ["MissingTable"], CancellationToken.None));

        Assert.Equal("Requested table 'MissingTable' was not found or was ambiguous.", error.Message);
    }

    [Fact]
    public async Task Approximate_mode_uses_postgres_catalog_sql()
    {
        var session = FakeSession.ForCatalog(("public", "Contracts", 123L));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["public.Contracts"], null, 50, false, 30));

        var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(
            new SqlHarnessCountReport("public", "Contracts", 123, "approx"),
            Assert.Single(report.Tables));
        Assert.Equal(0, report.Omitted);
        Assert.Equal(PostgresCounts.CatalogSql, Assert.Single(session.Commands).Sql);
        Assert.DoesNotContain("\"public\".\"Contracts\"", session.Commands[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_mode_issues_quoted_count_star_batch()
    {
        var session = FakeSession.ForCatalog(("public", "Contracts", 123L))
            .WithExactCounts(456L);

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["public.Contracts"], null, 50, true, 30));

        var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(
            new SqlHarnessCountReport("public", "Contracts", 456, "exact"),
            Assert.Single(report.Tables));
        Assert.Equal(2, session.Commands.Count);
        Assert.Equal(PostgresCounts.CatalogSql, session.Commands[0].Sql);

        var exact = session.Commands[1];
        Assert.Empty(exact.Parameters);
        Assert.Equal(30, exact.TimeoutSeconds);
        Assert.Contains("COUNT(*)", exact.Sql, StringComparison.Ordinal);
        Assert.Contains("\"public\".\"Contracts\"", exact.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COUNT_BIG", exact.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[public]", exact.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_resolution_failure_never_executes_exact_batch()
    {
        var session = FakeSession.ForCatalog()
            .WithExactCounts(999L);

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["MissingTable"], null, 50, true, 30));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("MissingTable", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(PostgresCounts.CatalogSql, Assert.Single(session.Commands).Sql);
    }

    [Fact]
    public async Task Approximate_like_mode_reports_omitted_and_binds_like()
    {
        var session = FakeSession.ForLikeCatalog(
            total: 5,
            ("public", "Big", 1000L),
            ("public", "Small", 1L));

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), [], "%x%", 2, false, 30));

        var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
        Assert.Equal(3, report.Omitted);
        Assert.Equal(
            [
                new SqlHarnessCountReport("public", "Big", 1000, "approx"),
                new SqlHarnessCountReport("public", "Small", 1, "approx"),
            ],
            report.Tables);
        Assert.Equal(PostgresCounts.CatalogSql, Assert.Single(session.Commands).Sql);
        Assert.Equal("%x%", Assert.Single(session.Commands[0].Parameters, p => p.Name == "@like").Value);
    }

    [Fact]
    public async Task ExecuteExactAsync_with_postgres_builder_returns_counts_in_order()
    {
        var objects = new[]
        {
            new ResolvedCountObject("public.B", "public", "B", 2, 1),
            new ResolvedCountObject("public.A", "public", "A", 1, 9),
        };
        var session = new FakeSession().WithExactCounts(10L, 20L);

        var counts = await CountsQuery.ExecuteExactAsync(
            session, objects, 30, CancellationToken.None, PostgresCounts.BuildExactSql);

        Assert.Equal([10L, 20L], counts);
        Assert.Contains("\"public\".\"B\"", session.Commands[0].Sql, StringComparison.Ordinal);
        Assert.Contains("\"public\".\"A\"", session.Commands[0].Sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*)", session.Commands[0].Sql, StringComparison.Ordinal);
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

    private static FakeReader CatalogReader(long total, params object?[][] rows) =>
        new(
            Set(["TotalObjects"], [total]),
            Set(["RequestedName", "SchemaName", "ObjectName", "ObjectId", "ApproxRows"], rows));

    private static object?[][] Set(string[] names, params object?[][] rows) =>
        [[.. names], .. rows];

    private sealed class FakeGain : IGainStore
    {
        public void Append(GainRecord record) { }
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSession : ISqlSessionFactory, ISqlSession
    {
        private readonly List<ISqlReader> _readers = [];
        private int _readerIndex;

        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("localhost", "appdb", "localhost", "appdb", "profile", Engine: "postgres");

        public static FakeSession ForCatalog(params (string Schema, string Name, long ApproxRows)[] tables)
        {
            var rows = new object?[tables.Length][];
            for (var i = 0; i < tables.Length; i++)
            {
                var t = tables[i];
                rows[i] = [$"{t.Schema}.{t.Name}", t.Schema, t.Name, i + 1, t.ApproxRows];
            }

            return new FakeSession().Enqueue(CatalogReader(0, rows));
        }

        public static FakeSession ForLikeCatalog(
            long total,
            params (string Schema, string Name, long ApproxRows)[] tables)
        {
            var rows = new object?[tables.Length][];
            for (var i = 0; i < tables.Length; i++)
            {
                var t = tables[i];
                rows[i] = [null, t.Schema, t.Name, i + 1, t.ApproxRows];
            }

            return new FakeSession().Enqueue(CatalogReader(total, rows));
        }

        public FakeSession WithExactCounts(params long[] counts)
        {
            if (counts.Length == 0)
                return this;

            var sets = new object?[counts.Length][][];
            for (var i = 0; i < counts.Length; i++)
                sets[i] = Set(["Ordinal", "Rows"], [i, counts[i]]);
            return Enqueue(new FakeReader(sets));
        }

        private FakeSession Enqueue(ISqlReader reader)
        {
            _readers.Add(reader);
            return this;
        }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            if (!string.Equals(target.Database, Identity.ActualDatabase, StringComparison.Ordinal))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (_readerIndex >= _readers.Count)
                throw new InvalidOperationException("No fake reader remaining for command.");
            return Task.FromResult(_readers[_readerIndex++]);
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
