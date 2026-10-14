using SqlHarness.Core;
using SqlHarness.Core.Dialect;

namespace SqlHarness.Tests;

public sealed class ParameterSetResolverTests
{
    private static readonly ISqlDialect Dialect = SqlDialects.For(SqlEngine.SqlServer);

    [Fact]
    public void Matrix_becomes_one_set_per_value()
    {
        var sets = ParameterSetResolver.Resolve(Dialect, [], new("BatchSize", "int", ["1", "20"]), null);
        Assert.Equal(["0", "1"], sets.Select(set => set.Name));
        Assert.Equal(["1", "20"], sets.Select(set => set.ParameterValue));
    }

    [Fact]
    public void Matrix_resolution_excludes_fixed_parameters()
    {
        var fixedParameters = Dialect.BindParameters([new SqlHarnessParameterInput("Tenant", "nvarchar", "acme")]);

        var sets = ParameterSetResolver.Resolve(Dialect, fixedParameters, new("BatchSize", "int", ["1", "20"]), null);

        Assert.All(sets, set => Assert.Equal(["@BatchSize"], set.Parameters.Select(parameter => parameter.Name)));
    }

    [Fact]
    public void Inline_sets_bind_typed_parameters_including_null()
    {
        var sets = ParameterSetResolver.Resolve(Dialect, [], null,
        [
            new("typical", []) { TypedParameters = [new("@ClientId", "int", "1039"), new("Note", "nvarchar", null)] },
            new("largest", []) { TypedParameters = [new("ClientId", "int", "4193"), new("Note", "nvarchar", "x")] },
        ]);
        Assert.Equal(DBNull.Value, sets[0].Parameters.Single(p => p.Name == "@Note").Value);
        Assert.Equal(4000, sets[0].Parameters.Single(p => p.Name == "@Note").Size);
    }

    [Fact]
    public void Inline_set_resolution_excludes_fixed_parameters()
    {
        var fixedParameters = Dialect.BindParameters([new SqlHarnessParameterInput("Tenant", "nvarchar", "acme")]);

        var sets = ParameterSetResolver.Resolve(Dialect, fixedParameters, null,
        [
            new("small", []) { TypedParameters = [new("ClientId", "int", "1")] },
            new("large", []) { TypedParameters = [new("ClientId", "int", "20")] },
        ]);

        Assert.All(sets, set => Assert.Equal(["@ClientId"], set.Parameters.Select(parameter => parameter.Name)));
    }

    [Fact]
    public void Sets_with_different_parameter_names_are_rejected()
    {
        var error = Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(Dialect, [], null,
        [
            new("a", []) { TypedParameters = [new("ClientId", "int", "1")] },
            new("b", []) { TypedParameters = [new("GroupId", "int", "1")] },
        ]));
        Assert.Contains("'b'", error.Message);
    }

    [Fact]
    public void Sets_with_different_types_for_one_name_are_rejected()
    {
        Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(Dialect, [], null,
        [
            new("a", []) { TypedParameters = [new("ClientId", "int", "1")] },
            new("b", []) { TypedParameters = [new("ClientId", "bigint", "1")] },
        ]));
    }

    [Fact]
    public void A_single_set_is_rejected_like_a_single_matrix_value()
    {
        Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(Dialect, [], null,
            [new("only", []) { TypedParameters = [new("ClientId", "int", "1")] }]));
    }

    [Fact]
    public void A_fixed_parameter_repeated_in_a_set_is_rejected()
    {
        var fixedParameters = Dialect.BindParameters([new SqlHarnessParameterInput("Date", "datetime", "2026-01-01T00:00:00")]);
        Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(Dialect, fixedParameters, null,
        [
            new("a", []) { TypedParameters = [new("Date", "datetime", "2026-01-02T00:00:00")] },
            new("b", []) { TypedParameters = [new("Date", "datetime", "2026-01-03T00:00:00")] },
        ]));
    }

    [Fact]
    public void Inline_sets_use_the_postgres_dialect_binder()
    {
        var dialect = SqlDialects.For(SqlEngine.Postgres);
        var sets = ParameterSetResolver.Resolve(dialect, [], null,
        [
            new("small", []) { TypedParameters = [new("ClientId", "int", "1")] },
            new("large", []) { TypedParameters = [new("ClientId", "int", "20")] },
        ]);

        Assert.Equal(1, sets[0].Parameters.Single().Value);
        Assert.Equal(20, sets[1].Parameters.Single().Value);
    }

    [Fact]
    public void Matrix_values_use_the_dialect_binder()
    {
        var dialect = SqlDialects.For(SqlEngine.Postgres);

        Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(
            dialect,
            [],
            new("Amount", "money", ["1", "2"]),
            null));
    }

    [Fact]
    public void Oversized_matrix_is_rejected_before_binding_values()
    {
        var values = new string?[33];
        values[0] = "not-an-int";
        for (var index = 1; index < values.Length; index++)
            values[index] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var error = Assert.Throws<SqlHarnessSafetyException>(() => ParameterSetResolver.Resolve(
            Dialect,
            [],
            new("BatchSize", "int", values),
            null));

        Assert.Contains("more than 32 values", error.Message);
    }
}
