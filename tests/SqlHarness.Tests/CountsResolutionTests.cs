using System.Data;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class CountsResolutionTests
{
    [Fact]
    public async Task Explicit_unqualified_name_must_resolve_once()
    {
        var reader = Reader(
            0,
            ["Contracts", "dbo", "Contracts", 42, 100L]);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, ["Contracts"], CancellationToken.None);

        Assert.Equal(new ResolvedCountObject(
            "Contracts", "dbo", "Contracts", 42, 100), Assert.Single(resolved.Objects));
        Assert.Equal(0, resolved.Omitted);
    }

    [Fact]
    public async Task Explicit_schema_qualified_name_resolves()
    {
        var reader = Reader(
            0,
            ["audit.Runs", "audit", "Runs", 7, 12L]);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, ["audit.Runs"], CancellationToken.None);

        Assert.Equal(
            new ResolvedCountObject("audit.Runs", "audit", "Runs", 7, 12),
            Assert.Single(resolved.Objects));
    }

    [Fact]
    public async Task Explicit_names_retain_input_order()
    {
        var reader = Reader(
            0,
            ["B", "dbo", "B", 2, 1L],
            ["A", "dbo", "A", 1, 9L]);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, ["B", "A"], CancellationToken.None);

        Assert.Equal(
            [
                new ResolvedCountObject("B", "dbo", "B", 2, 1),
                new ResolvedCountObject("A", "dbo", "A", 1, 9),
            ],
            resolved.Objects);
    }

    [Fact]
    public async Task Unknown_explicit_name_throws_safety_with_requested_name()
    {
        var reader = Reader(0);

        var error = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            CountsQuery.ReadCatalogAsync(reader, ["MissingTable"], CancellationToken.None));

        Assert.Equal("Requested table 'MissingTable' was not found or was ambiguous.", error.Message);
    }

    [Fact]
    public async Task Ambiguous_unqualified_name_throws_safety()
    {
        var reader = Reader(
            0,
            ["Contracts", "dbo", "Contracts", 1, 10L],
            ["Contracts", "sales", "Contracts", 2, 20L]);

        var error = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            CountsQuery.ReadCatalogAsync(reader, ["Contracts"], CancellationToken.None));

        Assert.Equal("Requested table 'Contracts' was not found or was ambiguous.", error.Message);
    }

    [Fact]
    public async Task Unsafe_requested_name_uses_generic_error_message()
    {
        var reader = Reader(0);
        var requested = "bad]name";

        var error = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            CountsQuery.ReadCatalogAsync(reader, [requested], CancellationToken.None));

        Assert.Equal("Requested table was not found or was ambiguous.", error.Message);
        Assert.DoesNotContain(requested, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("quote'name")]
    [InlineData("wild%card")]
    [InlineData("under_score_ok")] // underscore is safe — control that safe names still quote
    public async Task Error_message_policy_for_special_characters(string requested)
    {
        var reader = Reader(0);
        var error = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            CountsQuery.ReadCatalogAsync(reader, [requested], CancellationToken.None));

        var safe = IsSafeRequestedName(requested);
        if (safe)
            Assert.Equal($"Requested table '{requested}' was not found or was ambiguous.", error.Message);
        else
            Assert.Equal("Requested table was not found or was ambiguous.", error.Message);
    }

    [Fact]
    public async Task Unicode_name_resolves_and_is_safe_in_error_when_missing_uses_generic_if_not_ascii_identifier()
    {
        var unicode = "TabelaŻółć";
        var reader = Reader(
            0,
            [unicode, "dbo", unicode, 99, 3L]);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, [unicode], CancellationToken.None);

        Assert.Equal(
            new ResolvedCountObject(unicode, "dbo", unicode, 99, 3),
            Assert.Single(resolved.Objects));

        var missing = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() =>
            CountsQuery.ReadCatalogAsync(Reader(0), [unicode], CancellationToken.None));
        // Non-ASCII letters are not treated as safe identifier characters for error embedding.
        Assert.Equal("Requested table was not found or was ambiguous.", missing.Message);
    }

    [Fact]
    public async Task Bracket_and_quote_names_resolve_when_catalog_returns_them()
    {
        var weird = "na]me";
        var reader = Reader(
            0,
            [weird, "dbo", weird, 5, 1L]);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, [weird], CancellationToken.None);

        Assert.Equal(
            new ResolvedCountObject(weird, "dbo", weird, 5, 1),
            Assert.Single(resolved.Objects));
    }

    [Fact]
    public async Task Duplicate_requested_names_case_insensitively_resolve_once()
    {
        var reader = Reader(
            0,
            ["Contracts", "dbo", "Contracts", 42, 100L]);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, ["Contracts", "contracts", "CONTRACTS"], CancellationToken.None);

        Assert.Equal(
            new ResolvedCountObject("Contracts", "dbo", "Contracts", 42, 100),
            Assert.Single(resolved.Objects));
    }

    [Fact]
    public async Task Like_mode_orders_by_rows_then_schema_and_name()
    {
        // Reader supplies already-ordered rows as the SQL would; omitted = total - returned.
        var reader = Reader(
            4,
            [null, "dbo", "Big", 1, 1000L],
            [null, "audit", "Mid", 2, 50L],
            [null, "dbo", "Mid", 3, 50L],
            [null, "dbo", "Small", 4, 1L]);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, [], CancellationToken.None);

        Assert.Equal(0, resolved.Omitted);
        Assert.Equal(
            [
                new ResolvedCountObject("", "dbo", "Big", 1, 1000),
                new ResolvedCountObject("", "audit", "Mid", 2, 50),
                new ResolvedCountObject("", "dbo", "Mid", 3, 50),
                new ResolvedCountObject("", "dbo", "Small", 4, 1),
            ],
            resolved.Objects);
    }

    [Fact]
    public async Task Default_top_n_mode_reports_omitted()
    {
        var reader = Reader(
            5,
            [null, "dbo", "A", 1, 100L],
            [null, "dbo", "B", 2, 90L]);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, [], CancellationToken.None);

        Assert.Equal(2, resolved.Objects.Count);
        Assert.Equal(3, resolved.Omitted);
    }

    [Fact]
    public async Task Like_mode_with_zero_matches_returns_empty()
    {
        var reader = Reader(0);

        var resolved = await CountsQuery.ReadCatalogAsync(
            reader, [], CancellationToken.None);

        Assert.Empty(resolved.Objects);
        Assert.Equal(0, resolved.Omitted);
    }

    [Fact]
    public void Catalog_parameters_bind_tables_json_like_and_top_without_interpolation()
    {
        var parameters = CountsQuery.CatalogParameters(
            ["Contracts", "audit.Runs", "na]me"],
            "%Sync%",
            50);

        Assert.Equal(3, parameters.Count);

        var tables = Assert.Single(parameters, p => p.Name == "@tables");
        Assert.Equal(SqlDbType.NVarChar, tables.Type);
        Assert.Equal(-1, tables.Size);
        var json = Assert.IsType<string>(tables.Value);
        Assert.Equal(
            JsonSerializer.Serialize(new[] { "Contracts", "audit.Runs", "na]me" }),
            json);
        Assert.DoesNotContain("Contracts", CountsQuery.CatalogSql, StringComparison.Ordinal);

        var like = Assert.Single(parameters, p => p.Name == "@like");
        Assert.Equal(SqlDbType.NVarChar, like.Type);
        Assert.Equal(4000, like.Size);
        Assert.Equal("%Sync%", like.Value);

        var top = Assert.Single(parameters, p => p.Name == "@top");
        Assert.Equal(SqlDbType.Int, top.Type);
        Assert.Equal(50, top.Value);
    }

    [Fact]
    public void Catalog_parameters_null_tables_and_like_when_default_top_mode()
    {
        var parameters = CountsQuery.CatalogParameters([], null, 50);

        Assert.Equal(DBNull.Value, Assert.Single(parameters, p => p.Name == "@tables").Value);
        Assert.Equal(DBNull.Value, Assert.Single(parameters, p => p.Name == "@like").Value);
        Assert.Equal(50, Assert.Single(parameters, p => p.Name == "@top").Value);
    }

    [Fact]
    public void Catalog_parameters_dedupe_tables_case_insensitively()
    {
        var parameters = CountsQuery.CatalogParameters(
            ["Contracts", "contracts", "audit.Runs"],
            null,
            10);

        var json = Assert.IsType<string>(Assert.Single(parameters, p => p.Name == "@tables").Value);
        Assert.Equal(JsonSerializer.Serialize(new[] { "Contracts", "audit.Runs" }), json);
    }

    [Fact]
    public void Catalog_sql_uses_openjson_parsename_and_partition_stats()
    {
        Assert.Contains("OPENJSON", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PARSENAME", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.tables", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.schemas", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.dm_db_partition_stats", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("index_id IN (0,1)", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@tables", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@like", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@top", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC(", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sp_executesql", CountsQuery.CatalogSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadCatalog_requires_total_result_set()
    {
        var reader = new FakeReader(
            Set(["RequestedName", "SchemaName", "ObjectName", "ObjectId", "ApproxRows"]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CountsQuery.ReadCatalogAsync(reader, [], CancellationToken.None));
    }

    private static bool IsSafeRequestedName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            name,
            @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$");

    private static FakeReader Reader(long total, params object?[][] rows) =>
        new(
            Set(["TotalObjects"], [total]),
            Set(["RequestedName", "SchemaName", "ObjectName", "ObjectId", "ApproxRows"], rows));

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