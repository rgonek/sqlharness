using System.Data;
using System.Globalization;

using Microsoft.SqlServer.Types;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

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
    public void Parse_accepts_one_scalar_matrix_value()
    {
        var matrix = SqlParameterMatrixParser.Parse("BatchSize:int=7", []);

        Assert.Equal("@BatchSize", matrix.Name);
        Assert.Equal(["7"], matrix.DisplayValues);
        Assert.Equal(7, Assert.IsType<int>(Assert.Single(matrix.Values).Value));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Matrix_parameter_set_count_boundaries_are_one_through_thirty_two(int count, bool accepted)
    {
        var values = Enumerable.Range(1, count).Select(value => (string?)value.ToString(CultureInfo.InvariantCulture)).ToArray();
        var matrix = new SqlHarnessParameterMatrixInput("BatchSize", "int", values);

        if (!accepted)
        {
            var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
                ParameterSetResolver.Resolve(SqlDialects.For(SqlEngine.SqlServer), [], matrix, null));
            if (count == 33)
            {
                Assert.Contains("split", exception.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("32", exception.Message, StringComparison.Ordinal);
                Assert.Contains("singleton", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            return;
        }

        var sets = ParameterSetResolver.Resolve(SqlDialects.For(SqlEngine.SqlServer), [], matrix, null);

        Assert.Equal(count, sets.Count);
        Assert.Equal(Enumerable.Range(0, count).Select(index => index.ToString(CultureInfo.InvariantCulture)), sets.Select(set => set.Name));
        Assert.Equal(Enumerable.Range(1, count).Select(value => value.ToString(CultureInfo.InvariantCulture)), sets.Select(set => set.ParameterValue));
    }

    [Fact]
    public void Matrix_name_accepts_an_optional_at_sign()
    {
        var matrix = SqlParameterMatrixParser.Parse("@BatchSize:int=1,20", []);

        Assert.Equal("@BatchSize", matrix.Name);
        Assert.All(matrix.Values, value => Assert.Equal("@BatchSize", value.Name));
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
            "The --matrix option for SQL parameter '@BatchSize' requires at least one value.",
            exception.Message);
        AssertDoesNotEcho(exception, input);
    }

    [Fact]
    public void Parse_accepts_one_value()
    {
        var matrix = SqlParameterMatrixParser.Parse("BatchSize:int=1", []);

        Assert.Equal(["1"], matrix.DisplayValues);
        Assert.Equal(1, Assert.IsType<int>(Assert.Single(matrix.Values).Value));
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
    [InlineData("BatchSize=1,2", "The --matrix option for SQL parameter '@BatchSize' requires a type.")]
    [InlineData("BatchSize:=1,2", "The --matrix option for SQL parameter '@BatchSize' requires a type.")]
    [InlineData("BatchSize:xml=1,2", "The --matrix option for SQL parameter '@BatchSize' is invalid.")]
    [InlineData("BatchSize:int=nope,2", "The --matrix option for SQL parameter '@BatchSize' of type 'int' with value 'nope' is invalid.")]
    [InlineData(":int=1,2", "The --matrix option is invalid.")]
    public void Parse_rejects_malformed_name_type_or_value(string input, string expected)
    {
        var exception = Reject(input);

        Assert.Equal(expected, exception.Message);
        if (!expected.Contains("with value", StringComparison.Ordinal))
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
    [InlineData("BatchSize:int=nope", "The --matrix option for SQL parameter '@BatchSize' of type 'int' with value 'nope' is invalid.")]
    [InlineData("BatchSize:int=1,1,nope", "The --matrix option for SQL parameter '@BatchSize' contains a duplicate value.")]
    [InlineData("BatchSize:int=nope,1,1", "The --matrix option for SQL parameter '@BatchSize' of type 'int' with value 'nope' is invalid.")]
    public void Legacy_matrix_reports_the_first_failing_check(string input, string expected)
    {
        var exception = Reject(input);

        Assert.Equal(expected, exception.Message);
    }

    // 012/final F3: this pins SqlParameterMatrixParser.Parse's own order (the matrix is
    // fully checked before any fixed declaration is even read, lazily, via Reject below).
    // That convenience method has no production caller (see its doc comment); production
    // binds the opposite way -- CompareOperationPreparer.PrepareFixed binds the fixed
    // parameters first, then SqlHarnessModule.ExecuteCompareMatrixAsync binds the matrix
    // through SqlParameterMatrixParser.Bind directly. This test does not pin that order.
    [Fact]
    public void Legacy_matrix_Parse_checks_itself_before_reading_any_fixed_declaration()
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
        Assert.Equal((SqlDbType.NVarChar, "x", 4000), (parameters[0].Type, parameters[0].Value, parameters[0].Size));
        Assert.Equal((SqlDbType.Int, 42, null), (parameters[1].Type, parameters[1].Value, parameters[1].Size));
        Assert.Equal((SqlDbType.NVarChar, DBNull.Value, 4000), (parameters[2].Type, parameters[2].Value, parameters[2].Size));
        Assert.Equal((SqlDbType.Int, DBNull.Value, null), (parameters[3].Type, parameters[3].Value, parameters[3].Size));
        Assert.Equal((SqlDbType.NVarChar, "null", 4000), (parameters[4].Type, parameters[4].Value, parameters[4].Size));
        Assert.Equal((SqlDbType.NVarChar, "", 4000), (parameters[5].Type, parameters[5].Value, parameters[5].Size));
        Assert.Equal((SqlDbType.NVarChar, "p:q=r,s", 4000), (parameters[6].Type, parameters[6].Value, parameters[6].Size));
        Assert.Equal((SqlDbType.VarChar, DBNull.Value, 5), (parameters[7].Type, parameters[7].Value, parameters[7].Size));
    }

    [Theory]
    [InlineData("name", "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.")]
    [InlineData(":null", "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.")]
    [InlineData("name:int:nil", "SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.")]
    [InlineData("=value", "Invalid SQL parameter name ''.")]
    [InlineData("bad-name:int=1", "Invalid SQL parameter name 'bad-name'.")]
    [InlineData("bad-name:null", "Invalid SQL parameter name 'bad-name'.")]
    [InlineData("bad-name:int:null", "Invalid SQL parameter name 'bad-name'.")]
    [InlineData("name:xml=1", "Unsupported SQL parameter type 'xml'.")]
    [InlineData("name:xml:null", "Unsupported SQL parameter type 'xml'.")]
    [InlineData("name::null", "Unsupported SQL parameter type ''.")]
    [InlineData("name:=1", "Unsupported SQL parameter type ''.")]
    [InlineData("name:null=1", "Unsupported SQL parameter type 'null'.")]
    [InlineData("name:int:x=1", "Unsupported SQL parameter type 'int:x'.")]
    [InlineData("name:nchar(max):null", "Unsupported SQL parameter type 'nchar(max)'.")]
    [InlineData("name:int=", "Invalid value '' for SQL parameter 'name' of type 'int'.")]
    [InlineData("name:int=nope", "Invalid value 'nope' for SQL parameter 'name' of type 'int'.")]
    public void Legacy_declaration_rejections_keep_their_text(string input, string expected)
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterParser.ParseOne(input));

        Assert.Equal(expected, exception.Message);
    }

    [Fact]
    public void Unsized_char_accepts_empty_string_with_default_size()
    {
        var parameter = SqlParameterParser.ParseOne("name:char=");

        Assert.Equal(SqlDbType.Char, parameter.Type);
        Assert.Equal(string.Empty, parameter.Value);
        Assert.Equal(4000, parameter.Size);
    }

    [Theory]
    [InlineData("a:int=nope", "broken", "Invalid value 'nope' for SQL parameter 'a' of type 'int'.")]
    [InlineData("a=1", "A:int=nope", "Invalid value 'nope' for SQL parameter 'A' of type 'int'.")]
    [InlineData("a=1", "A:int=2", "Duplicate SQL parameter '@A'.")]
    public void Legacy_declaration_list_reports_the_first_failing_declaration(string first, string second, string expected)
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterParser.Parse([first, second]));

        Assert.Equal(expected, exception.Message);
    }

    // 012/T1: the typed model. No comma split, no empty-value rule, explicit null.
    [Theory]
    [InlineData("a=x", "a", null, "x")]
    [InlineData("a=", "a", null, "")]
    [InlineData("a=null", "a", null, "null")]
    [InlineData("a:null", "a", null, null)]
    [InlineData("a:int:null", "a", "int", null)]
    [InlineData("a:decimal(10,2)=1.5", "a", "decimal(10,2)", "1.5")]
    [InlineData("a:nvarchar=p:q=r,s", "a", "nvarchar", "p:q=r,s")]
    [InlineData("a:int:x=1", "a", "int:x", "1")]
    [InlineData("bad-name=1", "bad-name", null, "1")]
    public void Declaration_text_maps_to_the_model(string input, string name, string? type, string? value)
    {
        Assert.Equal(new SqlHarnessParameterInput(name, type, value), SqlParameterParser.ToInput(input));
    }

    [Fact]
    public void Matrix_text_maps_to_the_model()
    {
        var model = SqlParameterMatrixParser.ToInput("Amount:decimal(10,2)=1.25,2.50");

        Assert.Equal("Amount", model.Name);
        Assert.Equal("decimal(10,2)", model.Type);
        Assert.Equal(["1.25", "2.50"], model.Values);
    }

    // 012/final F8: the model records' generated ToString() must never print Value/Values --
    // only Name and Type, so a logged/thrown model instance cannot leak a secret.
    [Fact]
    public void Model_parameter_input_ToString_never_prints_the_value()
    {
        var input = new SqlHarnessParameterInput("Tenant", "nvarchar", "se,cret");

        var text = input.ToString();

        Assert.DoesNotContain("se,cret", text, StringComparison.Ordinal);
        Assert.Contains("Tenant", text, StringComparison.Ordinal);
        Assert.Contains("nvarchar", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_matrix_input_ToString_never_prints_the_values()
    {
        var matrix = new SqlHarnessParameterMatrixInput("BatchSize", "int", ["se,cret", "other-secret"]);

        var text = matrix.ToString();

        Assert.DoesNotContain("se,cret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret", text, StringComparison.Ordinal);
        Assert.Contains("BatchSize", text, StringComparison.Ordinal);
        Assert.Contains("int", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_parameter_value_is_bound_whole_and_null_is_explicit()
    {
        var parameters = SqlParameterParser.Bind(
        [
            new SqlHarnessParameterInput("a", null, "p:q=r,s"),
            new SqlHarnessParameterInput("b", "nvarchar(20)", ""),
            new SqlHarnessParameterInput("c", "int", null),
            new SqlHarnessParameterInput("d", null, null),
            new SqlHarnessParameterInput("e", "nvarchar", "null"),
        ]);

        Assert.Equal(["@a", "@b", "@c", "@d", "@e"], parameters.Select(parameter => parameter.Name));
        Assert.Equal("p:q=r,s", parameters[0].Value);
        Assert.Equal("", parameters[1].Value);
        Assert.Equal((SqlDbType.Int, DBNull.Value), (parameters[2].Type, parameters[2].Value));
        Assert.Equal((SqlDbType.NVarChar, DBNull.Value), (parameters[3].Type, parameters[3].Value));
        Assert.Equal("null", parameters[4].Value);
    }

    [Theory]
    [InlineData("a:b", "int", "1", "Invalid SQL parameter name 'a:b'.")]
    [InlineData("a=b", null, "1", "Invalid SQL parameter name 'a=b'.")]
    [InlineData("a", "", "1", "Unsupported SQL parameter type ''.")]
    [InlineData("a", "int", "", "Invalid value '' for SQL parameter 'a' of type 'int'.")]
    [InlineData("a", "int", "private,audit", "Invalid value 'private,audit' for SQL parameter 'a' of type 'int'.")]
    public void Model_parameter_rejections_are_the_declaration_rejections(string name, string? type, string value, string expected)
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(
            () => SqlParameterParser.Bind(new SqlHarnessParameterInput(name, type, value)));

        Assert.Equal(expected, exception.Message);
    }

    // 012/T3: decimal(p,s) on the typed path keeps the comma in the type token
    // (never a value separator) and still enforces precision/scale.
    [Fact]
    public void Model_parameter_decimal_type_token_binds_a_valid_value_and_rejects_an_out_of_range_value()
    {
        var parameter = SqlParameterParser.Bind(new SqlHarnessParameterInput("amount", "decimal(10,2)", "12345678.90"));

        Assert.Equal(SqlDbType.Decimal, parameter.Type);
        Assert.Equal(12345678.90m, parameter.Value);
        Assert.Equal((byte)10, parameter.Precision);
        Assert.Equal((byte)2, parameter.Scale);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterParser.Bind(
            new SqlHarnessParameterInput("amount", "decimal(10,2)", "123456789.00")));

        Assert.Equal("Invalid value '123456789.00' for SQL parameter 'amount' of type 'decimal(10,2)'.", exception.Message);
    }

    // 012/T3: the duplicate-name check on Bind(IEnumerable<...>) is case-insensitive
    // on the typed path too, with no declaration text involved.
    [Fact]
    public void Model_parameter_bind_rejects_duplicate_names_ignoring_case()
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterParser.Bind(
        [
            new SqlHarnessParameterInput("Tenant", "int", "1"),
            new SqlHarnessParameterInput("TENANT", "int", "2"),
        ]));

        Assert.Equal("Duplicate SQL parameter '@TENANT'.", exception.Message);
    }

    [Fact]
    public void Model_matrix_keeps_comma_empty_null_and_null_text_as_distinct_values()
    {
        var matrix = SqlParameterMatrixParser.Bind(
            new SqlHarnessParameterMatrixInput("Label", "nvarchar(10)", ["a,b", "", null, "null"]),
            ["@Tenant"]);

        Assert.Equal("@Label", matrix.Name);
        Assert.Equal("nvarchar(10)", matrix.Type);
        Assert.Equal(["a,b", "", null, "null"], matrix.DisplayValues);
        Assert.Equal<object>(["a,b", "", DBNull.Value, "null"], matrix.Values.Select(value => value.Value));
        Assert.All(matrix.Values, value => Assert.Equal((SqlDbType.NVarChar, 10), (value.Type, value.Size)));
    }

    [Fact]
    public void Model_matrix_rejects_two_nulls_as_a_duplicate()
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterMatrixParser.Bind(
            new SqlHarnessParameterMatrixInput("Label", "nvarchar(10)", [null, "x", null]),
            []));

        Assert.Equal("The --matrix option for SQL parameter '@Label' contains a duplicate value.", exception.Message);
    }

    [Theory]
    [InlineData("BatchSize", "int", "1", "nope", "The --matrix option for SQL parameter '@BatchSize' of type 'int' with value 'nope' is invalid.")]
    [InlineData("BatchSize", "int", "", "2", "The --matrix option for SQL parameter '@BatchSize' of type 'int' with value '' is invalid.")]
    [InlineData("BatchSize", "xml", "1", "2", "The --matrix option for SQL parameter '@BatchSize' is invalid.")]
    [InlineData("BatchSize", "", "1", "2", "The --matrix option for SQL parameter '@BatchSize' requires a type.")]
    [InlineData("bad:name", "int", "1", "2", "The --matrix option is invalid.")]
    [InlineData("BatchSize", "int", "1", "01", "The --matrix option for SQL parameter '@BatchSize' contains a duplicate value.")]
    [InlineData("Tenant", "int", "1", "2", "The --matrix option for SQL parameter '@Tenant' duplicates a fixed parameter.")]
    public void Model_matrix_rejections_are_the_text_rejections(string name, string type, string first, string second, string expected)
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterMatrixParser.Bind(
            new SqlHarnessParameterMatrixInput(name, type, [first, second]),
            ["@tenant"]));

        Assert.Equal(expected, exception.Message);
    }

    [Fact]
    public void Model_matrix_requires_at_least_one_value()
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterMatrixParser.Bind(
            new SqlHarnessParameterMatrixInput("BatchSize", "int", []),
            []));

        Assert.Equal("The --matrix option for SQL parameter '@BatchSize' requires at least one value.", exception.Message);
    }

    [Fact]
    public void Model_matrix_without_a_value_list_is_a_safety_rejection()
    {
        var exception = Assert.Throws<SqlHarnessSafetyException>(() => SqlParameterMatrixParser.Bind(
            new SqlHarnessParameterMatrixInput("BatchSize", "int", null!),
            []));

        Assert.Equal("The --matrix option for SQL parameter '@BatchSize' requires at least one value.", exception.Message);
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