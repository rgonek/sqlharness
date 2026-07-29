using System.Data;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SpaceQueryTests
{
    [Fact]
    public void Space_batch_is_fixed_read_only_and_binds_every_input()
    {
        var parameters = SpaceQuery.Parameters(25, "dbo", "Contracts");

        Assert.DoesNotContain("DBCC", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER ", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHRINK", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contracts", SpaceQuery.Sql, StringComparison.Ordinal);
        Assert.Collection(parameters,
            p => Assert.Equal("@top", p.Name),
            p => Assert.Equal("@objectSchema", p.Name),
            p => Assert.Equal("@objectName", p.Name));
    }

    [Fact]
    public void Space_sql_references_required_catalogs_and_deterministic_order()
    {
        Assert.Contains("sys.database_files", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FILEPROPERTY(name,'SpaceUsed')", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.dm_db_partition_stats", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.tables", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.schemas", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.indexes", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.partitions", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("index_id IN (0,1)", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST(", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decimal(19,2)", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("* 8 / 1024", SpaceQuery.Sql, StringComparison.Ordinal);
        Assert.Contains("TOP (@top)", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIXED", SpaceQuery.Sql, StringComparison.Ordinal);
        Assert.Contains("@objectSchema", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@objectName", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC(", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sp_executesql", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sp_spaceused", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);

        // Deterministic ordering by reserved pages then schema/name.
        Assert.Contains(
            "ORDER BY ReservedPages DESC, SchemaName, ObjectName",
            SpaceQuery.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parameters_bind_top_schema_and_name_with_nulls_as_dbnull()
    {
        var bound = SpaceQuery.Parameters(40, "audit", "Runs");
        Assert.Collection(bound,
            p =>
            {
                Assert.Equal("@top", p.Name);
                Assert.Equal(SqlDbType.Int, p.Type);
                Assert.Equal(40, p.Value);
            },
            p =>
            {
                Assert.Equal("@objectSchema", p.Name);
                Assert.Equal(SqlDbType.NVarChar, p.Type);
                Assert.Equal("audit", p.Value);
                Assert.Equal(128, p.Size);
            },
            p =>
            {
                Assert.Equal("@objectName", p.Name);
                Assert.Equal(SqlDbType.NVarChar, p.Type);
                Assert.Equal("Runs", p.Value);
                Assert.Equal(128, p.Size);
            });

        var nulls = SpaceQuery.Parameters(25, null, null);
        Assert.Equal(DBNull.Value, Assert.Single(nulls, p => p.Name == "@objectSchema").Value);
        Assert.Equal(DBNull.Value, Assert.Single(nulls, p => p.Name == "@objectName").Value);
        Assert.Equal(25, Assert.Single(nulls, p => p.Name == "@top").Value);
    }

    [Fact]
    public void Parameters_unqualified_object_binds_name_only()
    {
        var parameters = SpaceQuery.Parameters(10, null, "Contracts");

        Assert.Equal(DBNull.Value, Assert.Single(parameters, p => p.Name == "@objectSchema").Value);
        Assert.Equal("Contracts", Assert.Single(parameters, p => p.Name == "@objectName").Value);
        Assert.DoesNotContain("Contracts", SpaceQuery.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Index_set_joins_partition_stats_to_partitions_by_partition_number()
    {
        // Multi-partition indexes would inflate SUM(ps.*_page_count) if partitions and
        // dm_db_partition_stats were joined only on (object_id, index_id) (P×P cross product).
        Assert.Contains(
            "ps.partition_number = p.partition_number",
            SpaceQuery.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ps.object_id = p.object_id",
            SpaceQuery.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ps.index_id = p.index_id",
            SpaceQuery.Sql,
            StringComparison.OrdinalIgnoreCase);
    }
}