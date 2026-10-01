using System.Data;
using System.Globalization;

using Microsoft.SqlServer.Types;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SqlParameterMatrixTests
{
    [Fact]
    public void Parse_preserves_user_order_and_shared_int_typing()
    {
        var matrix = SqlParameterMatrixParser.Parse("BatchSize:int=1,20,100", []);

        Assert.Equal("@BatchSize", matrix.Name);
        Assert.Equal("int", matrix.Type);
        Assert.Equal(["1", "20", "100"], matrix.DisplayValues);
        Assert.Equal([1, 20, 100], matrix.Values.Select(value => (int)value.Value).ToArray());
        Assert.All(matrix.Values, value =>
        {
            Assert.Equal("@BatchSize", value.Name);
            Assert.Equal(SqlDbType.Int, value.Type);
            Assert.Null(value.Size);
            Assert.Null(value.Precision);
            Assert.Null(value.Scale);
        });
        Assert.Equal("@BatchSize", matrix.Spec.Name);
        Assert.Equal("int", matrix.Spec.Type);
        Assert.Equal(["1", "20", "100"], matrix.Spec.DisplayValues);
    }

    [Fact]
    public void Decimal_type_comma_is_not_a_value_separator()
    {
        using var _ = new TemporaryCulture("pl-PL");

        var matrix = SqlParameterMatrixParser.Parse("Amount:decimal(10,2)=1.25,2.50", []);

        Assert.Equal("@Amount", matrix.Name);
        Assert.Equal("decimal(10,2)", matrix.Type);
        Assert.Equal(["1.25", "2.50"], matrix.DisplayValues);
        Assert.Equal([1.25m, 2.50m], matrix.Values.Select(value => (decimal)value.Value).ToArray());
        Assert.All(matrix.Values, value =>
        {
            Assert.Equal("@Amount", value.Name);
            Assert.Equal(SqlDbType.Decimal, value.Type);
            Assert.Equal((byte)10, value.Precision);
            Assert.Equal((byte)2, value.Scale);
        });
        Assert.Equal("@Amount", matrix.Spec.Name);
        Assert.Equal("decimal(10,2)", matrix.Spec.Type);
        Assert.Equal(["1.25", "2.50"], matrix.Spec.DisplayValues);
    }

    [Fact]
    public void Parse_preserves_original_display_text()
    {
        var matrix = SqlParameterMatrixParser.Parse("BatchSize:int=01,2", []);

        Assert.Equal(["01", "2"], matrix.DisplayValues);
        Assert.Equal([1, 2], matrix.Values.Select(value => (int)value.Value).ToArray());
        Assert.Equal(["01", "2"], matrix.Spec.DisplayValues);
    }

    [Fact]
    public void Parse_splits_values_only_after_the_first_equals()
    {
        var matrix = SqlParameterMatrixParser.Parse("Filter:nvarchar=a=b,c=d", []);

        Assert.Equal("@Filter", matrix.Name);
        Assert.Equal("nvarchar", matrix.Type);
        Assert.Equal(["a=b", "c=d"], matrix.DisplayValues);
        Assert.Equal(["a=b", "c=d"], matrix.Values.Select(value => (string)value.Value).ToArray());
    }

    [Fact]
    public void Parse_allows_an_unrelated_fixed_parameter()
    {
        var matrix = SqlParameterMatrixParser.Parse(
            "BatchSize:int=1,20",
            ["AsOf:datetime2=2026-07-29T12:00:00"]);

        Assert.Equal("@BatchSize", matrix.Name);
        Assert.Equal([1, 20], matrix.Values.Select(value => (int)value.Value).ToArray());
    }

    [Fact]
    public void Parse_rejects_matrix_without_equals()
    {
        const string input = "BatchSize:int";

        var exception = Reject(input);

        Assert.Equal(
            "The --matrix option for SQL parameter '@BatchSize' must use name:type=value,value syntax.",
            exception.Message);
        AssertDoesNotEcho(exception, input);
    }

    [Fact]
    public void Parse_rejects_zero_values()
    {
        const string input = "BatchSize:int=";

        var exception = Reject(input);

        Assert.Equal(
            "The --matrix option for SQL parameter '@BatchSize' requires at least two values.",
            exception.Message);
        AssertDoesNotEcho(exception, input);
    }

    [Fact]
    public void Parse_rejects_one_value()
    {
        const string input = "BatchSize:int=1";

        var exception = Reject(input);

        Assert.Equal(
            "The --matrix option for SQL parameter '@BatchSize' requires at least two values.",
            exception.Message);
        AssertDoesNotEcho(exception, input, "1");
    }

    [Theory]
    [InlineData("BatchSize:int=1,")]
    [InlineData("BatchSize:int=,1")]
    [InlineData("BatchSize:int=1,,20")]
    public void Parse_rejects_empty_values(string input)
    {
        var exception = Reject(input);

        Assert.Equal(
            "The --matrix option for SQL parameter '@BatchSize' contains an empty value.",
            exception.Message);
        AssertDoesNotEcho(exception, input);
    }

    [Theory]
    [InlineData("BatchSize:int=1,01", "@BatchSize", "01")]
    [InlineData("BatchSize:int=1,2,1", "@BatchSize", "1,2,1")]
    [InlineData("Amount:decimal(10,2)=1.50,1.5", "@Amount", "1.50")]
    [InlineData("Flag:bit=true,1", "@Flag", "true")]
    [InlineData("Label:nvarchar=same,same", "@Label", "same")]
    public void Parse_rejects_duplicate_typed_values(string input, string parameterName, string displayValue)
    {
        using var _ = new TemporaryCulture("pl-PL");

        var exception = Reject(input);

        Assert.Equal(
            $"The --matrix option for SQL parameter '{parameterName}' contains a duplicate value.",
            exception.Message);
        AssertDoesNotEcho(exception, input, displayValue);
    }

    [Fact]
    public void Parse_keeps_datetime2_values_that_differ_by_a_fractional_second()
    {
        var matrix = SqlParameterMatrixParser.Parse(
            "At:datetime2=2026-07-29T12:00:00.0000001,2026-07-29T12:00:00.0000002",
            []);

        Assert.Equal("@At", matrix.Name);
        Assert.Equal(
            ["2026-07-29T12:00:00.0000001", "2026-07-29T12:00:00.0000002"],
            matrix.DisplayValues);
        Assert.Equal(
            [
                new DateTime(2026, 7, 29, 12, 0, 0).AddTicks(1),
                new DateTime(2026, 7, 29, 12, 0, 0).AddTicks(2),
            ],
            matrix.Values.Select(value => (DateTime)value.Value).ToArray());
    }

    [Fact]
    public void Parse_keeps_geography_values_that_differ_by_srid()
    {
        var matrix = SqlParameterMatrixParser.Parse(
            "Loc:geography=4326;POINT(-122.3 47.6),4269;POINT(-122.3 47.6)",
            []);

        Assert.Equal("@Loc", matrix.Name);
        Assert.Equal(
            ["4326;POINT(-122.3 47.6)", "4269;POINT(-122.3 47.6)"],
            matrix.DisplayValues);
        var first = Assert.IsType<SqlGeography>(matrix.Values[0].Value);
        var second = Assert.IsType<SqlGeography>(matrix.Values[1].Value);
        Assert.Equal(4326, first.STSrid.Value);
        Assert.Equal(4269, second.STSrid.Value);
    }

    [Fact]
    public void Parse_rejects_matrix_name_duplicated_by_fixed_parameter_ignoring_case()
    {
        const string input = "BatchSize:int=1,20";

        var exception = Reject(input, "batchsize:nvarchar=not-echoed");

        Assert.Equal(
            "The --matrix option for SQL parameter '@BatchSize' duplicates a fixed parameter.",
            exception.Message);
        AssertDoesNotEcho(exception, input, "1", "20", "not-echoed");
    }

    [Theory]
    [InlineData("bad-name:int=1,2", "The --matrix option is invalid.")]
    [InlineData("@BatchSize:int=1,2", "The --matrix option is invalid.")]
    [InlineData("BatchSize=1,2", "The --matrix option for SQL parameter '@BatchSize' requires a type.")]
    [InlineData("BatchSize:=1,2", "The --matrix option for SQL parameter '@BatchSize' requires a type.")]
    [InlineData("BatchSize:xml=1,2", "The --matrix option for SQL parameter '@BatchSize' is invalid.")]
    [InlineData("BatchSize:int=nope,2", "The --matrix option for SQL parameter '@BatchSize' of type 'int' is invalid.")]
    [InlineData(":int=1,2", "The --matrix option is invalid.")]
    public void Parse_rejects_malformed_name_type_or_value(string input, string expected)
    {
        var exception = Reject(input);

        Assert.Equal(expected, exception.Message);
        AssertDoesNotEcho(exception, input, "1", "2", "nope", "xml", "bad-name");
    }

    // 012/T1 characterization: the legacy text grammar, pinned before the typed model was put under it.
    [Fact]
    public void Legacy_matrix_text_null_is_a_text_value_and_a_comma_always_separates()
    {
        var matrix = SqlParameterMatrixParser.Parse("Label:nvarchar=null,a b,c:d", []);

        Assert.Equal(["null", "a b", "c:d"], matrix.DisplayValues);
        Assert.Equal(["null", "a b", "c:d"], matrix.Values.Select(value => (string)value.Value).ToArray());
    }

    [Fact]
    public void Legacy_matrix_type_is_everything_after_the_first_colon()
    {
        var exception = Reject("BatchSize:int:x=1,2");

        Assert.Equal("The --matrix option for SQL parameter '@BatchSize' is invalid.", exception.Message);
    }

    [Theory]
    [InlineData("BatchSize:=", "The --matrix option for SQL parameter '@BatchSize' requires a type.")]
    [InlineData("BatchSize:xml=1,,2", "The --matrix option for SQL parameter '@BatchSize' contains an empty value.")]
    [InlineData("BatchSize:int=nope", "The --matrix option for SQL parameter '@BatchSize' requires at least two values.")]
    [InlineData("BatchSize:int=1,1,nope", "The --matrix option for SQL parameter '@BatchSize' contains a duplicate value.")]
    [InlineData("BatchSize:int=nope,1,1", "The --matrix option for SQL parameter '@BatchSize' of type 'int' is invalid.")]
    public void Legacy_matrix_reports_the_first_failing_check(string input, string expected)
    {
        var exception = Reject(input);

        Assert.Equal(expected, exception.Message);
    }

    [Fact]
    public void Legacy_matrix_checks_itself_before_the_fixed_parameters()
    {
        var matrixFirst = Reject("BatchSize:int=1,1", "broken");
        var fixedAfter = Reject("BatchSize:int=1,2", "Tenant:int=7", "broken");

        Assert.Equal(
            "The --matrix option for SQL parameter '@BatchSize' contains a duplicate value.",
            matrixFirst.Message);
        Assert.Equal(
            "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.",
            fixedAfter.Message);
    }

    [Fact]
    public void Legacy_declaration_forms_bind_value_text_null_and_typed_null()
    {
        var parameters = SqlParameterParser.Parse(
            ["a=x", "b:int=42", "c:null", "d:int:null", "e=null", "f=", "g:nvarchar=p:q=r,s", "h:varchar(5):null"]);

        Assert.Equal(["@a", "@b", "@c", "@d", "@e", "@f", "@g", "@h"], parameters.Select(parameter => parameter.Name));
        Assert.Equal((SqlDbType.NVarChar, "x", 1), (parameters[0].Type, parameters[0].Value, parameters[0].Size));
        Assert.Equal((SqlDbType.Int, 42, null), (parameters[1].Type, parameters[1].Value, parameters[1].Size));
        Assert.Equal((SqlDbType.NVarChar, DBNull.Value, null), (parameters[2].Type, parameters[2].Value, parameters[2].Size));
        Assert.Equal((SqlDbType.Int, DBNull.Value, null), (parameters[3].Type, parameters[3].Value, parameters[3].Size));
        Assert.Equal((SqlDbType.NVarChar, "null", 4), (parameters[4].Type, parameters[4].Value, parameters[4].Size));
        Assert.Equal((SqlDbType.NVarChar, "", 1), (parameters[5].Type, parameters[5].Value, parameters[5].Size));
        Assert.Equal((SqlDbType.NVarChar, "p:q=r,s", 7), (parameters[6].Type, parameters[6].Value, parameters[6].Size));
        Assert.Equal((SqlDbType.VarChar, DBNull.Value, 5), (parameters[7].Type, parameters[7].Value, parameters[7].Size));
    }

    [Theory]
    [InlineData("name", "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.")]
    [InlineData(":null", "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.")]
    [InlineData("name:int:nil", "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.")]
    [InlineData("=value", "Invalid SQL parameter name.")]
    [InlineData("bad-name:int=1", "Invalid SQL parameter name.")]
    [InlineData("bad-name:null", "Invalid SQL parameter name.")]
    [InlineData("bad-name:int:null", "Invalid SQL parameter name.")]
    [InlineData("name:xml=1", "Unsupported SQL parameter type 'xml'.")]
    [InlineData("name:xml:null", "Unsupported SQL parameter type 'xml'.")]
    [InlineData("name::null", "Unsupported SQL parameter type ''.")]
    [InlineData("name:=1", "Unsupported SQL parameter type ''.")]
    [InlineData("name:null=1", "Unsupported SQL parameter type 'null'.")]
    [InlineData("name:int:x=1", "Unsupported SQL parameter type 'int:x'.")]
    [InlineData("name:nchar(max):null", "Unsupported SQL parameter type 'nchar(max)'.")]
    [InlineData("name:int=", "Invalid value for SQL parameter 'name' of type 'int'.")]
    [InlineData("name:int=nope", "Invalid value for SQL parameter 'name' of type 'int'.")]
    [InlineData("name:char=", "Invalid value for SQL parameter 'name' of type 'char'.")]
    public void Legacy_declaration_rejections_keep_their_text(string input, string expected)
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterParser.ParseOne(input));

        Assert.Equal(expected, exception.Message);
    }

    [Theory]
    [InlineData("a:int=nope", "broken", "Invalid value for SQL parameter 'a' of type 'int'.")]
    [InlineData("a=1", "A:int=nope", "Invalid value for SQL parameter 'A' of type 'int'.")]
    [InlineData("a=1", "A:int=2", "Duplicate SQL parameter '@A'.")]
    public void Legacy_declaration_list_reports_the_first_failing_declaration(string first, string second, string expected)
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterParser.Parse([first, second]));

        Assert.Equal(expected, exception.Message);
    }

    private static SqlHarnessSafetyException Reject(string input, params string[] fixedParameters) =>
        Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterMatrixParser.Parse(input, fixedParameters));

    private static void AssertDoesNotEcho(SqlHarnessSafetyException exception, string input, params string[] fragments)
    {
        Assert.DoesNotContain(input, exception.Message, StringComparison.Ordinal);
        foreach (var fragment in fragments)
            Assert.DoesNotContain(fragment, exception.Message, StringComparison.Ordinal);
    }

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