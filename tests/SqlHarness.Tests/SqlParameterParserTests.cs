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
        Assert.Null(parameter.Precision);
        Assert.Null(parameter.Scale);
    }

    [Fact]
    public void Parse_binds_explicit_decimal_precision_and_scale()
    {
        var parameter = Assert.Single(SqlParameterParser.Parse(["amount:decimal(19,4)=1234.5600"]));
        Assert.Equal(SqlDbType.Decimal, parameter.Type);
        Assert.Equal((byte)19, parameter.Precision);
        Assert.Equal((byte)4, parameter.Scale);
        Assert.Equal(1234.5600m, parameter.Value);
    }

    [Theory]
    [InlineData("at:datetime=2026-07-29T12:00:00", SqlDbType.DateTime)]
    [InlineData("at:datetime2=2026-07-29T12:00:00.1234567", SqlDbType.DateTime2)]
    [InlineData("at:datetimeoffset=2026-07-29T12:00:00+02:00", SqlDbType.DateTimeOffset)]
    public void Parse_binds_supported_temporal_types(string input, SqlDbType expected)
    {
        var parameter = Assert.Single(SqlParameterParser.Parse([input]));
        Assert.Equal(expected, parameter.Type);
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
    public void Parse_binds_datetimeoffset_with_explicit_zulu()
    {
        var parameter = Assert.Single(SqlParameterParser.Parse(["at:datetimeoffset=2026-07-29T12:00:00Z"]));

        Assert.Equal(SqlDbType.DateTimeOffset, parameter.Type);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero), parameter.Value);
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

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    public void Parse_binds_uniqueidentifier_in_any_guid_format(string format)
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var formatted = id.ToString(format);

        var parameter = Assert.Single(SqlParameterParser.Parse([$"id:uniqueidentifier={formatted}"]));

        Assert.Equal(id, parameter.Value);
        Assert.Equal(SqlDbType.UniqueIdentifier, parameter.Type);
    }

    [Theory]
    [InlineData("status:smallint=32000", SqlDbType.SmallInt, (short)32000)]
    [InlineData("flag:tinyint=255", SqlDbType.TinyInt, (byte)255)]
    public void Parse_binds_small_integral_types(string input, SqlDbType type, object expected)
    {
        var parameter = Assert.Single(SqlParameterParser.Parse([input]));
        Assert.Equal(type, parameter.Type);
        Assert.Equal(expected, parameter.Value);
    }

    [Fact]
    public void Parse_binds_float_real_money_and_time()
    {
        var parameters = SqlParameterParser.Parse([
            "ratio:float=1.5",
            "approx:real=2.5",
            "price:money=19.99",
            "fee:smallmoney=1.25",
            "at:time=14:30:00.1234567",
            "when:smalldatetime=2026-07-29T12:00:00"]);

        Assert.Equal(SqlDbType.Float, parameters[0].Type);
        Assert.Equal(1.5d, parameters[0].Value);
        Assert.Equal(SqlDbType.Real, parameters[1].Type);
        Assert.Equal(2.5f, parameters[1].Value);
        Assert.Equal(SqlDbType.Money, parameters[2].Type);
        Assert.Equal(19.99m, parameters[2].Value);
        Assert.Equal(SqlDbType.SmallMoney, parameters[3].Type);
        Assert.Equal(1.25m, parameters[3].Value);
        Assert.Equal(SqlDbType.Time, parameters[4].Type);
        Assert.Equal(TimeSpan.Parse("14:30:00.1234567", CultureInfo.InvariantCulture), parameters[4].Value);
        Assert.Equal(SqlDbType.SmallDateTime, parameters[5].Type);
        Assert.Equal(new DateTime(2026, 7, 29, 12, 0, 0), parameters[5].Value);
    }

    [Fact]
    public void Parse_binds_numeric_alias_and_string_variants()
    {
        var longText = new string('x', 4001);
        var parameters = SqlParameterParser.Parse([
            "amount:numeric(10,2)=12.34",
            "plain:numeric=9.5",
            "ascii:varchar=hello",
            "wide:nvarchar(max)=" + longText,
            "fixed:char(5)=abc",
            "path:hierarchyid=/1/2/",
            "shape:geometry=POINT(1 2)"]);

        Assert.Equal(SqlDbType.Decimal, parameters[0].Type);
        Assert.Equal((byte)10, parameters[0].Precision);
        Assert.Equal((byte)2, parameters[0].Scale);
        Assert.Equal(12.34m, parameters[0].Value);
        Assert.Equal(SqlDbType.Decimal, parameters[1].Type);
        Assert.Equal(SqlDbType.VarChar, parameters[2].Type);
        Assert.Equal(5, parameters[2].Size);
        Assert.Equal(SqlDbType.NVarChar, parameters[3].Type);
        Assert.Equal(-1, parameters[3].Size);
        Assert.Equal(longText, parameters[3].Value);
        Assert.Equal(SqlDbType.Char, parameters[4].Type);
        Assert.Equal(5, parameters[4].Size);
        Assert.Equal(SqlDbType.Udt, parameters[5].Type);
        Assert.Equal("HierarchyId", parameters[5].UdtTypeName);
        Assert.Equal(SqlDbType.Udt, parameters[6].Type);
        Assert.Equal("Geometry", parameters[6].UdtTypeName);
    }

    [Fact]
    public void Parse_binds_geography_with_optional_srid_prefix()
    {
        var plain = Assert.Single(SqlParameterParser.Parse([
            "loc:geography=POINT(-122.34900 47.65100)"]));
        Assert.Equal(SqlDbType.Udt, plain.Type);
        Assert.Equal("Geography", plain.UdtTypeName);

        var withSrid = Assert.Single(SqlParameterParser.Parse([
            "loc:geography=4326;POINT(-122.34900 47.65100)"]));
        Assert.Equal(SqlDbType.Udt, withSrid.Type);
        Assert.Equal("Geography", withSrid.UdtTypeName);
    }

    [Fact]
    public void Parse_binds_udt_typed_null()
    {
        var parameter = Assert.Single(SqlParameterParser.Parse(["path:hierarchyid:null"]));
        Assert.Equal(SqlDbType.Udt, parameter.Type);
        Assert.Equal("HierarchyId", parameter.UdtTypeName);
        Assert.Same(DBNull.Value, parameter.Value);
    }

    [Fact]
    public void Parse_binds_varbinary_from_base64()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var parameter = Assert.Single(SqlParameterParser.Parse([
            $"blob:varbinary={Convert.ToBase64String(bytes)}"]));

        Assert.Equal(SqlDbType.VarBinary, parameter.Type);
        Assert.Equal(bytes, parameter.Value);
        Assert.Equal(4, parameter.Size);
    }

    [Fact]
    public void Parse_promotes_long_untyped_string_to_nvarchar_max()
    {
        var longText = new string('y', 5000);
        var parameter = Assert.Single(SqlParameterParser.Parse([$"note={longText}"]));

        Assert.Equal(SqlDbType.NVarChar, parameter.Type);
        Assert.Equal(-1, parameter.Size);
        Assert.Equal(longText, parameter.Value);
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
    public void Parse_binds_typed_null()
    {
        var parameter = Assert.Single(SqlParameterParser.Parse(["count:int:null"]));

        Assert.Equal("@count", parameter.Name);
        Assert.Equal(SqlDbType.Int, parameter.Type);
        Assert.Same(DBNull.Value, parameter.Value);
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
    [InlineData("amount:decimal(0,0)=0")]
    [InlineData("amount:decimal(39,0)=1")]
    [InlineData("amount:decimal(10,11)=1")]
    [InlineData("amount:decimal(5,2)=12345.67")]
    [InlineData("at:datetimeoffset=2026-07-29T12:00:00")]
    [InlineData("at:datetimeoffset=2026-07-29T12:00:00+99:00")]
    [InlineData("at:datetime=1752-12-31T00:00:00")]
    [InlineData("at:datetime=10000-01-01T00:00:00")]
    [InlineData("flag:tinyint=256")]
    [InlineData("status:smallint=40000")]
    [InlineData("ratio:float=NaN")]
    [InlineData("fee:smallmoney=300000")]
    [InlineData("when:smalldatetime=1899-12-31T00:00:00")]
    [InlineData("blob:varbinary=not-base64!!")]
    [InlineData("fixed:char(2)=abc")]
    [InlineData("path:hierarchyid=")]
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