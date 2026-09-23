using System.Data;
using System.Text.RegularExpressions;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class QueryStoreTopQueryTests
{
    private const string IntervalPredicate = """
        WHERE rsi.start_time < SYSUTCDATETIME()
          AND rsi.end_time >= DATEADD(minute, -@windowMinutes, SYSUTCDATETIME())
          AND rs.execution_type = 0
        """;

    private const string RankingOrder = """
        ORDER BY
            total_duration_milliseconds DESC,
            total_cpu_milliseconds DESC,
            execution_count DESC,
            query_id ASC
        """;

    private const string MetricColumns = """
        query_id,
            query_hash,
            object_name,
            execution_count,
            plan_count,
            total_duration_milliseconds,
            average_duration_milliseconds,
            maximum_duration_milliseconds,
            total_cpu_milliseconds,
            average_cpu_milliseconds,
            maximum_cpu_milliseconds,
            total_logical_reads,
            average_logical_reads,
            maximum_logical_reads,
            last_execution_at
        """;

    private static readonly string[] RequiredCatalogs =
    [
        "sys.database_query_store_options",
        "sys.query_store_query",
        "sys.query_store_query_text",
        "sys.query_store_plan",
        "sys.query_store_runtime_stats",
        "sys.query_store_runtime_stats_interval",
        "sys.objects",
        "sys.schemas",
    ];

    [Fact]
    public void Batch_is_fixed_read_only_and_binds_window_and_top()
    {
        var parameters = QueryStoreTopQuery.Parameters(windowMinutes: 120, top: 30);

        Assert.DoesNotContain("ALTER ", QueryStoreTopQuery.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPTION (RECOMPILE)", QueryStoreTopQuery.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avg_duration * rs.count_executions",
            QueryStoreTopQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avg_cpu_time * rs.count_executions",
            QueryStoreTopQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avg_logical_io_reads * rs.count_executions",
            QueryStoreTopQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(parameters,
            p => { Assert.Equal("@windowMinutes", p.Name); Assert.Equal(120, p.Value); },
            p => { Assert.Equal("@top", p.Name); Assert.Equal(30, p.Value); });
    }

    [Fact]
    public void Parameters_bind_window_and_top_as_ints()
    {
        var parameters = QueryStoreTopQuery.Parameters(windowMinutes: 15, top: 5);

        Assert.Collection(parameters,
            p =>
            {
                Assert.Equal("@windowMinutes", p.Name);
                Assert.Equal(SqlDbType.Int, p.Type);
                Assert.Equal(15, p.Value);
                Assert.Null(p.Size);
            },
            p =>
            {
                Assert.Equal("@top", p.Name);
                Assert.Equal(SqlDbType.Int, p.Type);
                Assert.Equal(5, p.Value);
                Assert.Null(p.Size);
            });
    }

    [Fact]
    public void Batch_references_only_the_named_query_store_catalogs()
    {
        var found = Regex.Matches(BatchSql, @"\bsys\.[A-Za-z0-9_]+")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            RequiredCatalogs.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            found.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(Normalize(IntervalPredicate), BatchSql, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(BatchSql, "rs.execution_type = 0"));
    }

    [Fact]
    public void Batch_weights_duration_and_cpu_in_milliseconds_and_reads_in_pages()
    {
        Assert.Contains(
            "SUM(rs.avg_duration * rs.count_executions) / 1000.0 AS total_duration_milliseconds",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "SUM(rs.avg_duration * rs.count_executions) / 1000.0 / NULLIF(SUM(rs.count_executions), 0) AS average_duration_milliseconds",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAX(rs.max_duration) / 1000.0 AS maximum_duration_milliseconds",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0 AS total_cpu_milliseconds",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0 / NULLIF(SUM(rs.count_executions), 0) AS average_cpu_milliseconds",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAX(rs.max_cpu_time) / 1000.0 AS maximum_cpu_milliseconds",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "SUM(rs.avg_logical_io_reads * rs.count_executions) AS total_logical_reads",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "SUM(rs.avg_logical_io_reads * rs.count_executions) / NULLIF(SUM(rs.count_executions), 0) AS average_logical_reads",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAX(rs.max_logical_io_reads) AS maximum_logical_reads",
            BatchSql,
            StringComparison.Ordinal);
        Assert.Contains("SUM(rs.count_executions) AS execution_count", BatchSql, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT p.plan_id) AS plan_count", BatchSql, StringComparison.Ordinal);
        Assert.Contains("MAX(rs.last_execution_time) AS last_execution_at", BatchSql, StringComparison.Ordinal);
        Assert.DoesNotContain("avg_logical_io_reads * rs.count_executions) / 1000.0", BatchSql, StringComparison.Ordinal);
        Assert.DoesNotContain("max_logical_io_reads) / 1000.0", BatchSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_uses_uppercase_hex_hash_and_schema_qualified_object_name()
    {
        Assert.Contains("CONVERT(varchar(16), q.query_hash, 2)", BatchSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0x", BatchSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN q.object_id = 0 THEN NULL", BatchSql, StringComparison.Ordinal);
        Assert.Contains("s.name + N'.' + o.name", BatchSql, StringComparison.Ordinal);
        Assert.Contains("sys.schemas", BatchSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.objects", BatchSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Batch_groups_by_query_id_and_orders_duration_cpu_executions_then_id()
    {
        var groupBy = Regex.Match(BatchSql, @"GROUP BY\s+q\.query_id\b", RegexOptions.IgnoreCase);
        Assert.True(groupBy.Success);
        Assert.Equal(2, CountOccurrences(BatchSql, Normalize(RankingOrder)));
        Assert.Contains("TOP (@top)", BatchSql, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(BatchSql, "INSERT INTO @topQueries"));
    }

    [Fact]
    public void Batch_materializes_one_ranking_and_returns_state_metrics_then_text()
    {
        var names = Regex.Matches(BatchSql, @"@[A-Za-z_][A-Za-z0-9_]*")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "@top", "@topQueries", "@windowMinutes" },
            names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray());

        var selects = Regex.Matches(BatchSql, @"\bSELECT\b", RegexOptions.IgnoreCase)
            .Select(match => match.Index)
            .ToArray();
        Assert.Equal(4, selects.Length);

        var insertAt = BatchSql.IndexOf("INSERT INTO @topQueries", StringComparison.Ordinal);
        Assert.True(insertAt >= 0 && insertAt < selects[0]);

        var ranking = BatchSql[selects[0]..selects[1]];
        Assert.Contains("TOP (@top)", ranking, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", ranking, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q.query_id", ranking, StringComparison.Ordinal);
        Assert.Contains(Normalize(RankingOrder), ranking, StringComparison.Ordinal);
        Assert.DoesNotContain("query_sql_text", ranking, StringComparison.OrdinalIgnoreCase);

        var state = BatchSql[selects[1]..selects[2]];
        Assert.Contains("actual_state_desc", state, StringComparison.Ordinal);
        Assert.Contains("sys.database_query_store_options", state, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(",", state, StringComparison.Ordinal);
        Assert.DoesNotContain("query_sql_text", state, StringComparison.OrdinalIgnoreCase);

        var metrics = BatchSql[selects[2]..selects[3]];
        Assert.Contains(Normalize(MetricColumns), metrics, StringComparison.Ordinal);
        Assert.Contains("FROM @topQueries", metrics, StringComparison.Ordinal);
        Assert.Contains(Normalize(RankingOrder), metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("query_sql_text", metrics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP (", metrics, StringComparison.OrdinalIgnoreCase);

        var text = BatchSql[selects[3]..];
        Assert.Contains(Normalize("""
            t.query_id,
                t.query_hash,
                qt.query_sql_text
            """), text, StringComparison.Ordinal);
        Assert.Contains("FROM @topQueries", text, StringComparison.Ordinal);
        Assert.Contains("q.query_id = t.query_id", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.query_store_query_text", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP (", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query_store_runtime_stats", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GROUP BY", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, CountOccurrences(BatchSql, "FROM @topQueries"));
    }

    private static string BatchSql => Normalize(QueryStoreTopQuery.Sql);

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
