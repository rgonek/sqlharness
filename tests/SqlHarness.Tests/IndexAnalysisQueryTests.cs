using System.Data;
using System.Text.RegularExpressions;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class IndexAnalysisQueryTests
{
    private const string Score =
        "(migs.user_seeks + migs.user_scans) * migs.avg_total_user_cost * (migs.avg_user_impact / 100.0)";

    private static readonly string[] RequiredIdentifiers =
    [
        "sys.dm_db_missing_index_details",
        "sys.dm_db_missing_index_groups",
        "sys.dm_db_missing_index_group_stats",
        "sys.dm_os_sys_info",
        "sys.tables",
        "sys.schemas",
        "sys.objects",
        "sys.indexes",
        "sys.index_columns",
        "sys.columns",
        "sys.partitions",
        "DB_ID()",
        "sqlserver_start_time",
    ];

    [Fact]
    public void Batch_is_read_only_parameterized_and_uses_exact_score()
    {
        var parameters = IndexAnalysisQuery.Parameters(20, "dbo", "Contracts");

        Assert.DoesNotContain("CREATE INDEX", IndexAnalysisQuery.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP INDEX", IndexAnalysisQuery.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(migs.user_seeks + migs.user_scans)",
            IndexAnalysisQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migs.avg_total_user_cost",
            IndexAnalysisQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migs.avg_user_impact / 100.0",
            IndexAnalysisQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(parameters,
            p => Assert.Equal("@top", p.Name),
            p => Assert.Equal("@objectSchema", p.Name),
            p => Assert.Equal("@objectName", p.Name));
    }

    [Fact]
    public void Parameters_bind_top_schema_and_table_with_nulls_as_dbnull()
    {
        var bound = IndexAnalysisQuery.Parameters(20, "dbo", "Contracts");
        Assert.Collection(bound,
            p =>
            {
                Assert.Equal("@top", p.Name);
                Assert.Equal(SqlDbType.Int, p.Type);
                Assert.Equal(20, p.Value);
                Assert.Null(p.Size);
            },
            p =>
            {
                Assert.Equal("@objectSchema", p.Name);
                Assert.Equal(SqlDbType.NVarChar, p.Type);
                Assert.Equal("dbo", p.Value);
                Assert.Equal(128, p.Size);
            },
            p =>
            {
                Assert.Equal("@objectName", p.Name);
                Assert.Equal(SqlDbType.NVarChar, p.Type);
                Assert.Equal("Contracts", p.Value);
                Assert.Equal(128, p.Size);
            });

        var nulls = IndexAnalysisQuery.Parameters(7, null, null);
        Assert.Equal(7, nulls[0].Value);
        Assert.Equal(DBNull.Value, nulls[1].Value);
        Assert.Equal(DBNull.Value, nulls[2].Value);

        var unqualified = IndexAnalysisQuery.Parameters(7, null, "Contracts");
        Assert.Equal(DBNull.Value, unqualified[1].Value);
        Assert.Equal("Contracts", unqualified[2].Value);
    }

    [Fact]
    public void Batch_names_every_catalog_and_captures_one_utc_timestamp()
    {
        var sql = BatchSql;
        foreach (var identifier in RequiredIdentifiers)
            Assert.Contains(identifier, sql, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, CountOccurrences(sql, "SYSUTCDATETIME"));
        Assert.Contains("DECLARE @observedAt datetime2(7) = SYSUTCDATETIME()", sql, StringComparison.Ordinal);
        Assert.Contains("@observedAt AS observed_at", sql, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(sql, "DB_ID()"));
        Assert.Equal(1, CountOccurrences(sql, "sqlserver_start_time"));
        Assert.Contains("database_id = DB_ID()", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Batch_does_not_interpolate_caller_schema_or_table_text()
    {
        var sql = IndexAnalysisQuery.Sql;
        var parameters = IndexAnalysisQuery.Parameters(20, "dbo", "Contracts");

        Assert.DoesNotContain("dbo", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contracts", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP INDEX", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER INDEX", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sp_executesql", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("dbo", parameters[1].Value);
        Assert.Equal("Contracts", parameters[2].Value);
    }

    [Fact]
    public void Batch_ranks_top_only_after_database_user_table_and_object_filters()
    {
        var (observation, ranking) = SplitObservation(ResultStatements()[0]);

        Assert.Contains("COUNT_BIG(*)", observation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS object_match_count", observation, StringComparison.Ordinal);
        Assert.Contains("o.type = 'U'", observation, StringComparison.Ordinal);
        Assert.Contains("o.is_ms_shipped = 0", observation, StringComparison.Ordinal);
        Assert.Contains("(@objectName IS NULL OR t.name = @objectName)", observation, StringComparison.Ordinal);
        Assert.Contains("(@objectSchema IS NULL OR s.name = @objectSchema)", observation, StringComparison.Ordinal);
        Assert.DoesNotContain("@objectName IS NOT NULL", observation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@candidates", observation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sys.dm_db_missing_index", observation, StringComparison.OrdinalIgnoreCase);
        AssertAliasOrder(observation, "observation_since", "observed_at", "object_match_count");

        Assert.Equal(1, CountOccurrences(ranking, "SELECT"));
        Assert.Equal(1, CountOccurrences(BatchSql, "TOP (@top)"));
        Assert.Contains("TOP (@top)", ranking, StringComparison.Ordinal);
        Assert.Contains("WHERE mid.database_id = DB_ID()", ranking, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("o.type = 'U'", ranking, StringComparison.Ordinal);
        Assert.Contains("o.is_ms_shipped = 0", ranking, StringComparison.Ordinal);
        Assert.Contains("(@objectName IS NULL OR t.name = @objectName)", ranking, StringComparison.Ordinal);
        Assert.Contains("(@objectSchema IS NULL OR s.name = @objectSchema)", ranking, StringComparison.Ordinal);
        Assert.Contains(Score, ranking, StringComparison.Ordinal);
        Assert.Contains("mid.index_handle AS candidate_id", ranking, StringComparison.Ordinal);

        var top = ranking.IndexOf("TOP (@top)", StringComparison.Ordinal);
        var where = ranking.IndexOf("WHERE mid.database_id = DB_ID()", StringComparison.OrdinalIgnoreCase);
        var order = ranking.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        Assert.True(top >= 0 && where > top && order > where);
        AssertAliasOrder(
            ranking[order..],
            "cumulative_impact_score DESC",
            "(migs.user_seeks + migs.user_scans) DESC",
            "schema_name ASC",
            "table_name ASC",
            "candidate_id ASC");
    }

    [Fact]
    public void Batch_returns_six_sets_and_limits_later_sets_to_ranked_tables()
    {
        var sets = ResultStatements();
        var candidates = sets[1];
        Assert.Equal(1, CountOccurrences(BatchSql, "INSERT INTO @candidates"));
        Assert.Contains("FROM @candidates", candidates, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP (", candidates, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object_id", SelectList(candidates), StringComparison.OrdinalIgnoreCase);
        AssertAliasOrder(
            SelectList(candidates),
            "candidate_id",
            "schema_name",
            "table_name",
            "equality_columns",
            "inequality_columns",
            "included_columns",
            "user_seeks",
            "user_scans",
            "avg_total_user_cost",
            "avg_user_impact",
            "cumulative_impact_score",
            "last_user_seek",
            "last_user_scan");
        AssertAliasOrder(
            candidates[candidates.IndexOf("ORDER BY", StringComparison.Ordinal)..],
            "cumulative_impact_score DESC",
            "(user_seeks + user_scans) DESC",
            "schema_name ASC",
            "table_name ASC",
            "candidate_id ASC");

        AssertAliasOrder(SelectList(sets[2]), "schema_name", "table_name", "column_name");
        Assert.Contains("col.column_id", sets[2], StringComparison.Ordinal);
        Assert.Contains("sys.columns", sets[2], StringComparison.OrdinalIgnoreCase);

        AssertAliasOrder(
            SelectList(sets[3]),
            "schema_name",
            "table_name",
            "index_id",
            "index_name",
            "type_desc",
            "is_unique",
            "is_primary_key",
            "is_unique_constraint",
            "is_disabled",
            "filter_definition");
        Assert.Contains("sys.indexes", sets[3], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("i.filter_definition", sets[3], StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE", sets[3], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HEAP", sets[3], StringComparison.OrdinalIgnoreCase);

        AssertAliasOrder(
            SelectList(sets[4]),
            "schema_name",
            "table_name",
            "index_id",
            "column_name",
            "key_ordinal",
            "is_included_column",
            "is_descending_key",
            "index_column_id");
        Assert.Contains("sys.index_columns", sets[4], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.columns", sets[4], StringComparison.OrdinalIgnoreCase);

        AssertAliasOrder(
            SelectList(sets[5]),
            "schema_name",
            "table_name",
            "index_id",
            "data_compression_desc");
        Assert.Contains("sys.partitions", sets[5], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MIXED", sets[5], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GROUP BY", sets[5], StringComparison.OrdinalIgnoreCase);

        foreach (var metadata in sets.Skip(2))
        {
            Assert.Contains("FROM @candidates", metadata, StringComparison.Ordinal);
            Assert.DoesNotContain("sys.dm_db_missing_index", metadata, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sys.tables", metadata, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TOP (", metadata, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(5, CountOccurrences(BatchSql, "FROM @candidates"));
        Assert.DoesNotContain("GROUP BY", BatchSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MIXED", BatchSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HASHBYTES", BatchSql, StringComparison.OrdinalIgnoreCase);
    }

    private static string BatchSql => Normalize(IndexAnalysisQuery.Sql);

    // Result statements are the only SELECT keywords at column 0. The ranking SELECT stays indented under INSERT.
    private static string[] ResultStatements()
    {
        var sql = BatchSql;
        var matches = Regex.Matches(sql, @"(?m)^SELECT\b");
        Assert.Equal(6, matches.Count);
        var slices = new string[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : sql.Length;
            slices[i] = sql[start..end];
        }

        return slices;
    }

    private static (string Observation, string Ranking) SplitObservation(string set1)
    {
        var insertAt = set1.IndexOf("INSERT INTO @candidates", StringComparison.Ordinal);
        Assert.True(insertAt > 0);
        return (set1[..insertAt], set1[insertAt..]);
    }

    private static string SelectList(string statement)
    {
        var from = statement.IndexOf("\nFROM", StringComparison.OrdinalIgnoreCase);
        Assert.True(from > 0);
        return statement[..from];
    }

    private static void AssertAliasOrder(string text, params string[] aliases)
    {
        var position = -1;
        foreach (var alias in aliases)
        {
            var found = text.IndexOf(alias, position + 1, StringComparison.Ordinal);
            Assert.True(found > position, $"Expected '{alias}' after position {position}.");
            position = found;
        }
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        var comparison = StringComparison.OrdinalIgnoreCase;
        while ((index = text.IndexOf(value, index, comparison)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}