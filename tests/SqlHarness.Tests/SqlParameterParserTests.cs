using System.Data;
using System.Globalization;
using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SqlParameterParserTests
{
    [Fact]
    public void Parse_binds_untyped_value_as_bounded_nvarchar()
    {
        var parameter = Assert.Single(SqlParameterParser.Parse(["name=zażółć"]));

        Assert.Equal("@name", parameter.Name);
        Assert.Equal(SqlDbType.NVarChar, parameter.Type);
        Assert.Equal("zażółć", parameter.Value);
        Assert.Equal(6, parameter.Size);
    }

    [Theory]
    [InlineData("count:int=42", SqlDbType.Int, 42)]
    [InlineData("count:bigint=9223372036854775807", SqlDbType.BigInt, 9223372036854775807L)]
    [InlineData("enabled:bit=true", SqlDbType.Bit, true)]
    public void Parse_binds_allowlisted_integral_and_boolean_types(string input, SqlDbType type, object expected)
    {
        var parameter = Assert.Single(SqlParameterParser.Parse([input]));

        Assert.Equal(type, parameter.Type);
        Assert.Equal(expected, parameter.Value);
        Assert.Null(parameter.Size);
    }

    [Fact]
    public void Parse_binds_decimal_using_invariant_culture()
    {
        using var _ = new TemporaryCulture("pl-PL");

        var parameter = Assert.Single(SqlParameterParser.Parse(["amount:decimal=1234.56"]));

        Assert.Equal(SqlDbType.Decimal, parameter.Type);
        Assert.Equal(1234.56m, parameter.Value);
    }

    [Fact]
    public void Parse_binds_date_and_datetime2_using_invariant_culture()
    {
        using var _ = new TemporaryCulture("pl-PL");

        var parameters = SqlParameterParser.Parse([
            "day:date=2026-07-13",
            "at:datetime2=2026-07-13T14:15:16.1234567"]);

        Assert.Equal(new DateTime(2026, 7, 13), parameters[0].Value);
        Assert.Equal(new DateTime(2026, 7, 13, 14, 15, 16).AddTicks(1_234_567), parameters[1].Value);
    }

    [Fact]
    public void Parse_binds_uniqueidentifier_and_explicit_nvarchar()
    {
        var id = Guid.NewGuid();

        var parameters = SqlParameterParser.Parse([$"id:uniqueidentifier={id:D}", "text:nvarchar=witaj"]);

        Assert.Equal(id, parameters[0].Value);
        Assert.Equal(SqlDbType.UniqueIdentifier, parameters[0].Type);
        Assert.Equal("witaj", parameters[1].Value);
        Assert.Equal(SqlDbType.NVarChar, parameters[1].Type);
        Assert.Equal(5, parameters[1].Size);
    }

    [Fact]
    public void Parse_binds_documented_null_as_DBNull()
    {
        var parameter = Assert.Single(SqlParameterParser.Parse(["missing:null"]));

        Assert.Equal("@missing", parameter.Name);
        Assert.Equal(SqlDbType.NVarChar, parameter.Type);
        Assert.Same(DBNull.Value, parameter.Value);
        Assert.Null(parameter.Size);
    }

    [Fact]
    public void Parse_preserves_equals_characters_in_values()
    {
        var parameter = Assert.Single(SqlParameterParser.Parse(["filter=a=b=c"]));

        Assert.Equal("a=b=c", parameter.Value);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("=value")]
    [InlineData("bad-name=value")]
    [InlineData("@name=value")]
    [InlineData("name:xml=<x />")]
    [InlineData("name:int=not-an-int")]
    [InlineData("name:bit=perhaps")]
    [InlineData("name:date=13/07/2026")]
    [InlineData("name:null=value")]
    public void Parse_rejects_malformed_or_unsupported_parameters(string input) =>
        Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterParser.Parse([input]));

    [Fact]
    public void Parse_rejects_duplicate_names_ordinal_ignore_case() =>
        Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterParser.Parse(["ClientId=1", "clientid=2"]));

    private sealed class TemporaryCulture : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

        public TemporaryCulture(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }
}
