using System.Data;

using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class CountsTests
{
    [Fact]
    public async Task Approximate_mode_returns_catalog_rows_without_dynamic_count()
    {
        var session = FakeSession.ForCatalog(("dbo", "Contracts", 123L));
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.Contracts"], null, 50, false, 30));

        var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(
            new SqlHarnessCountReport("dbo", "Contracts", 123, "approx"),
            Assert.Single(report.Tables));
        Assert.Equal(0, report.Omitted);
        Assert.Same(session.Identity, report.Target);
        Assert.Single(session.Commands);
        Assert.Equal(CountsQuery.CatalogSql, session.Commands[0].Sql);
        // Approximate mode must not issue a second dynamic COUNT_BIG batch.
        Assert.DoesNotContain("FROM [dbo].[Contracts]", session.Commands[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_mode_issues_second_command_with_quoted_names_and_count_big()
    {
        var session = FakeSession.ForCatalog(("dbo", "Contracts", 123L))
            .WithExactCounts(456L);

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.Contracts"], null, 50, true, 30));

        var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(
            new SqlHarnessCountReport("dbo", "Contracts", 456, "exact"),
            Assert.Single(report.Tables));
        Assert.Equal(2, session.Commands.Count);
        Assert.Equal(CountsQuery.CatalogSql, session.Commands[0].Sql);

        var exact = session.Commands[1];
        Assert.Empty(exact.Parameters);
        Assert.Equal(30, exact.TimeoutSeconds);
        Assert.Contains("COUNT_BIG(*)", exact.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[dbo].[Contracts]", exact.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENJSON", exact.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sys.dm_db_partition_stats", exact.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC(", exact.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sp_executesql", exact.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exact_mode_retains_input_order_for_explicit_tables()
    {
        var session = FakeSession.ForCatalog(
                ("dbo", "B", 1L),
                ("dbo", "A", 9L))
            .WithExactCounts(10L, 20L);

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.B", "dbo.A"], null, 50, true, 30));

        var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
        Assert.Equal(
            [
                new SqlHarnessCountReport("dbo", "B", 10, "exact"),
                new SqlHarnessCountReport("dbo", "A", 20, "exact"),
            ],
            report.Tables);

        var exactSql = session.Commands[1].Sql;
        var indexB = exactSql.IndexOf("[dbo].[B]", StringComparison.Ordinal);
        var indexA = exactSql.IndexOf("[dbo].[A]", StringComparison.Ordinal);
        Assert.True(indexB >= 0 && indexA > indexB);
        Assert.Contains("CAST(0 AS int)", exactSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST(1 AS int)", exactSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exact_sql_quotes_closing_brackets_in_identifiers()
    {
        var session = FakeSession.ForCatalog(("dbo", "na]me", 1L))
            .WithExactCounts(7L);

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.na]me"], null, 50, true, 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var exactSql = session.Commands[1].Sql;
        Assert.Contains("[dbo].[na]]me]", exactSql, StringComparison.Ordinal);
        Assert.Equal(
            new SqlHarnessCountReport("dbo", "na]me", 7, "exact"),
            Assert.Single(Assert.IsType<SqlHarnessCountsReport>(outcome.Report).Tables));
    }

    [Fact]
    public async Task Exact_sql_failure_maps_to_five()
    {
        var session = FakeSession.ForCatalog(("dbo", "Contracts", 123L));
        session.FailOnCommandIndex = 1;
        session.ExecuteFailure = FakeSqlException();

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.Contracts"], null, 50, true, 30));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(2, session.Commands.Count);
        Assert.Contains("COUNT_BIG(*)", session.Commands[1].Sql, StringComparison.OrdinalIgnoreCase);
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
        Assert.Single(session.Commands);
        Assert.Equal(CountsQuery.CatalogSql, session.Commands[0].Sql);
    }

    [Fact]
    public async Task Approximate_like_mode_reports_omitted_and_method_approx()
    {
        var session = FakeSession.ForLikeCatalog(
            total: 5,
            ("dbo", "Big", 1000L),
            ("dbo", "Small", 1L));

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), [], "%x%", 2, false, 30));

        var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
        Assert.Equal(3, report.Omitted);
        Assert.Equal(
            [
                new SqlHarnessCountReport("dbo", "Big", 1000, "approx"),
                new SqlHarnessCountReport("dbo", "Small", 1, "approx"),
            ],
            report.Tables);
        Assert.Single(session.Commands);
        Assert.Equal("%x%", Assert.Single(session.Commands[0].Parameters, p => p.Name == "@like").Value);
    }

    [Fact]
    public async Task Exact_mode_with_empty_catalog_skips_exact_batch()
    {
        var session = FakeSession.ForLikeCatalog(total: 0)
            .WithExactCounts();

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), [], null, 50, true, 30));

        var report = Assert.IsType<SqlHarnessCountsReport>(outcome.Report);
        Assert.Empty(report.Tables);
        Assert.Equal(0, report.Omitted);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task Counts_target_mismatch_maps_to_four_without_commands()
    {
        var session = FakeSession.ForCatalog(("dbo", "Contracts", 1L));
        session.ConnectFailure = new SqlTargetMismatchException("mismatch");

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.Contracts"], null, 50, false, 30));

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Counts_rejects_out_of_range_timeout_and_top_as_safety()
    {
        var session = FakeSession.ForCatalog(("dbo", "Contracts", 1L));

        var badTimeout = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.Contracts"], null, 50, false, 0));
        Assert.Equal(SqlHarnessExitCode.Safety, badTimeout.ExitCode);
        Assert.Empty(session.Commands);

        var badTop = await Module(session).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.Contracts"], null, 0, false, 30));
        Assert.Equal(SqlHarnessExitCode.Safety, badTop.ExitCode);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Counts_records_gain_with_counts_command()
    {
        var session = FakeSession.ForCatalog(("dbo", "Contracts", 123L));
        var gain = new FakeGain();

        var outcome = await Module(session, gain).ExecuteAsync(
            new SqlHarnessCountsOperation(Target(), ["dbo.Contracts"], null, 50, false, 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(10, 1));
        Assert.Equal("counts", Assert.Single(gain.Records).Command);
    }

    [Fact]
    public async Task ExecuteExactAsync_returns_counts_in_object_order()
    {
        var objects = new[]
        {
            new ResolvedCountObject("dbo.B", "dbo", "B", 2, 1),
            new ResolvedCountObject("dbo.A", "dbo", "A", 1, 9),
        };
        var session = new FakeSession().WithExactCounts(10L, 20L);

        var counts = await CountsQuery.ExecuteExactAsync(session, objects, 30, CancellationToken.None);

        Assert.Equal([10L, 20L], counts);
        Assert.Contains("[dbo].[B]", session.Commands[0].Sql, StringComparison.Ordinal);
        Assert.Contains("[dbo].[A]", session.Commands[0].Sql, StringComparison.Ordinal);
    }

    private static SqlHarnessModule Module(FakeSession session, FakeGain? gain = null) =>
        new(session, gain ?? new FakeGain(), Profiles);

    private static SqlTargetRequest Target() => new("test", new Dictionary<string, string>());

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("server", "db", new Dictionary<string, string>(), "integrated"),
        };

    private static SqlException FakeSqlException() =>
        (SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SqlException));

    private sealed class FakeGain : IGainStore
    {
        public List<GainRecord> Records { get; } = [];
        public void Append(GainRecord record) => Records.Add(record);
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSession : ISqlSessionFactory, ISqlSession
    {
        private readonly List<ISqlReader> _readers = [];
        private int _readerIndex;

        public Exception? ConnectFailure { get; set; }
        public Exception? ExecuteFailure { get; set; }
        public int? FailOnCommandIndex { get; set; }
        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "db", "server", "db", "profile");

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

        private static FakeReader CatalogReader(long total, object?[][] rows) =>
            new(
                Set(["TotalObjects"], [total]),
                Set(["RequestedName", "SchemaName", "ObjectName", "ObjectId", "ApproxRows"], rows));

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct) =>
            ConnectFailure is null
                ? Task.FromResult<ISqlSession>(this)
                : Task.FromException<ISqlSession>(ConnectFailure);

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (FailOnCommandIndex is int failIndex
                && Commands.Count - 1 == failIndex
                && ExecuteFailure is not null)
            {
                return Task.FromException<ISqlReader>(ExecuteFailure);
            }

            if (_readerIndex >= _readers.Count)
                throw new InvalidOperationException("No fake reader remaining for command.");

            return Task.FromResult(_readers[_readerIndex++]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static object?[][] Set(string[] names, params object?[][] rows) =>
        [[.. names], .. rows];

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