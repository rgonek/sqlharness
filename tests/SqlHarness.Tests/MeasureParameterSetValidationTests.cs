using System.Globalization;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class MeasureParameterSetValidationTests
{
    private const string Secret = "super-secret-value";

    [Fact]
    public void Prepare_combines_fixed_and_set_parameters_without_values_in_metadata()
    {
        var sets = MeasureParameterSetValidator.Prepare(
            ["tenant:nvarchar=acme"],
            [new("small", ["id:int=1"]), new("large", ["id:int=999"])],
            null, "select @tenant, @id");

        Assert.Equal(["small", "large"], sets.Select(x => x.Name));
        Assert.All(sets, x => Assert.Equal(["@id", "@tenant"],
            x.Metadata.Select(m => m.Name)));
        Assert.DoesNotContain("999", JsonSerializer.Serialize(
            sets.Select(x => new { x.Name, x.Metadata, x.ValueHash })));

        Assert.Equal(
            [new MeasureParameterMetadata("@id", "int"), new MeasureParameterMetadata("@tenant", "nvarchar")],
            sets[0].Metadata);
        Assert.Equal(sets[0].Metadata, sets[1].Metadata);
        Assert.NotEqual(sets[0].ValueHash, sets[1].ValueHash);
        Assert.Matches("^[0-9A-F]{64}$", sets[0].ValueHash);
        Assert.Matches("^[0-9A-F]{64}$", sets[1].ValueHash);
        Assert.DoesNotContain("0x", sets[0].ValueHash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Assert.IsType<int>(sets[0].Parameters.Single(parameter => parameter.Name == "@id").Value));
        Assert.Equal("acme", sets[0].Parameters.Single(parameter => parameter.Name == "@tenant").Value);
        Assert.Equal(999, Assert.IsType<int>(sets[1].Parameters.Single(parameter => parameter.Name == "@id").Value));
        Assert.DoesNotContain("acme", JsonSerializer.Serialize(
            sets.Select(x => new { x.Name, x.Metadata, x.ValueHash })), StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_rejects_duplicate_set_names_ignoring_case_without_values()
    {
        var exception = Reject(
            [],
            [new("Small", [$"id:int=515151"]), new("SMALL", [$"label:nvarchar(30)={Secret}"])],
            null,
            "select @id, @label");

        Assert.Equal("Duplicate parameter set 'SMALL'.", exception.Message);
        AssertSafe(exception, Secret, "515151", "id:int=515151", "label:nvarchar(30)=");
    }

    [Fact]
    public void Prepare_rejects_fixed_and_set_parameter_collisions_ignoring_case()
    {
        var exception = Reject(
            [$"id:int=616161"],
            [new("wide", ["ID:int=626262"]), new("tall", [$"id:int=636363"])],
            null,
            "select @id");

        Assert.Equal("Parameter set 'wide' repeats fixed SQL parameter '@ID'.", exception.Message);
        AssertSafe(exception, "616161", "626262", "636363", "id:int=616161", "ID:int=626262");
    }

    [Fact]
    public void Prepare_rejects_duplicate_names_within_a_set_without_values()
    {
        var exception = Reject(
            [],
            [new("small", ["id:int=515151", "ID:int=525252"]), new("large", ["id:int=1"])],
            null,
            "select @id");

        Assert.Equal("Parameter set 'small' is invalid: Duplicate SQL parameter '@ID'.", exception.Message);
        AssertSafe(exception, "515151", "525252", "id:int=515151", "ID:int=525252");
    }

    [Fact]
    public void Prepare_rejects_duplicate_fixed_parameters_without_values()
    {
        var exception = Reject(
            ["id:int=515151", "ID:int=525252"],
            [new("small", ["other:int=1"]), new("large", ["other:int=2"])],
            null,
            "select @id, @other");

        Assert.Equal("Duplicate SQL parameter '@ID'.", exception.Message);
        AssertSafe(exception, "515151", "525252", "id:int=515151", "ID:int=525252");
    }

    [Fact]
    public void Prepare_rejects_a_set_missing_a_parameter()
    {
        var exception = Reject(
            [],
            [new("small", ["id:int=1", $"label:nvarchar(30)={Secret}"]), new("large", ["id:int=2"])],
            null,
            "select @id, @label");

        Assert.Equal("Parameter set 'large' is missing SQL parameter '@label'.", exception.Message);
        AssertSafe(exception, Secret, "label:nvarchar(30)=");
    }

    [Fact]
    public void Prepare_rejects_a_set_with_an_unexpected_parameter()
    {
        var exception = Reject(
            [],
            [new("small", ["id:int=1"]), new("large", ["id:int=2", $"extra:nvarchar(30)={Secret}"])],
            null,
            "select @id");

        Assert.Equal("Parameter set 'large' has unexpected SQL parameter '@extra'.", exception.Message);
        AssertSafe(exception, Secret, "extra:nvarchar(30)=");
    }

    [Fact]
    public void Prepare_rejects_different_sql_types_across_sets()
    {
        var exception = Reject(
            [],
            [new("small", ["id:int=717171"]), new("large", ["id:bigint=717171"])],
            null,
            "select @id");

        Assert.Equal(
            "Parameter set 'large' SQL parameter '@id' has a different type than parameter set 'small'.",
            exception.Message);
        AssertSafe(exception, "717171", "id:int=717171", "id:bigint=717171");
    }

    [Fact]
    public void Prepare_rejects_different_udt_types_across_sets()
    {
        var exception = Reject(
            [],
            [new("small", ["loc:geography=POINT(1 2)"]), new("large", ["loc:geometry=POINT(3 4)"])],
            null,
            "select @loc");

        Assert.Equal(
            "Parameter set 'large' SQL parameter '@loc' has a different type than parameter set 'small'.",
            exception.Message);
        AssertSafe(exception, "POINT(1 2)", "POINT(3 4)", "loc:geography=", "loc:geometry=");
    }

    [Fact]
    public void Prepare_rejects_different_sizes_across_sets()
    {
        var exception = Reject(
            [],
            [
                new("small", [$"label:nvarchar(20)={Secret}"]),
                new("large", [$"label:nvarchar(40)={Secret}"]),
            ],
            null,
            "select @label");

        Assert.Equal(
            "Parameter set 'large' SQL parameter '@label' has a different size than parameter set 'small'.",
            exception.Message);
        AssertSafe(exception, Secret, "nvarchar(20)", "nvarchar(40)", "label:nvarchar(20)=", "label:nvarchar(40)=");
    }

    [Theory]
    [InlineData("name:nvarchar=a", "name:nvarchar=abcd", "nvarchar")]
    [InlineData("name:nvarchar:null", "name:nvarchar=abcd", "nvarchar")]
    [InlineData("name:varchar=a", "name:varchar=abcd", "varchar")]
    [InlineData("name:varchar:null", "name:varchar=abcd", "varchar")]
    [InlineData("name:varbinary=QQ==", "name:varbinary=AQID", "varbinary")]
    [InlineData("name:varbinary:null", "name:varbinary=AQID", "varbinary")]
    [InlineData("name:varbinary(8)=QQ==", "name:varbinary(8)=AQID", "varbinary")]
    [InlineData("name:char=a", "name:char=abcd", "char")]
    [InlineData("name:char:null", "name:char=abcd", "char")]
    [InlineData("name:nchar=a", "name:nchar=abcd", "nchar")]
    [InlineData("name:nchar:null", "name:nchar=abcd", "nchar")]
    [InlineData("name:binary(4)=QQ==", "name:binary(4)=AQID", "binary")]
    public void Prepare_accepts_different_values_when_the_declared_type_matches(string left, string right, string type)
    {
        var sets = MeasureParameterSetValidator.Prepare(
            [],
            [new("small", [left]), new("large", [right])],
            null,
            "select @name");

        Assert.Equal([new MeasureParameterMetadata("@name", type)], sets[0].Metadata);
        Assert.Equal(sets[0].Metadata, sets[1].Metadata);
        Assert.NotEqual(sets[0].ValueHash, sets[1].ValueHash);
        var safe = JsonSerializer.Serialize(sets.Select(set => new { set.Name, set.Metadata, set.ValueHash }));
        Assert.DoesNotContain(left, safe, StringComparison.Ordinal);
        Assert.DoesNotContain(right, safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_keeps_bound_size_in_the_hash_for_unsized_nvarchar()
    {
        var sets = MeasureParameterSetValidator.Prepare(
            [],
            [new("small", ["name:nvarchar=a"]), new("large", ["name:nvarchar=abcd"])],
            null,
            "select @name");

        Assert.Equal(1, sets[0].Parameters.Single().Size);
        Assert.Equal(4, sets[1].Parameters.Single().Size);
        Assert.Equal("a", sets[0].Parameters.Single().Value);
        Assert.Equal("abcd", sets[1].Parameters.Single().Value);
        Assert.NotEqual(sets[0].ValueHash, sets[1].ValueHash);
    }

    [Theory]
    [InlineData("label:nvarchar(max)=secret-aa", "label:nvarchar=secret-bb", "select @label", "size")]
    [InlineData("label:nvarchar(max)=secret-aa", "label:nvarchar(10)=secret-bb", "select @label", "size")]
    [InlineData("label:nvarchar(10)=secret-aa", "label:nvarchar(20)=secret-bb", "select @label", "size")]
    [InlineData("amount:decimal(19,4)=12345.6700", "amount:decimal(10,2)=12345.67", "select @amount", "precision")]
    public void Prepare_rejects_different_declared_sizes_and_decimal_precision(
        string left, string right, string query, string field)
    {
        var exception = Reject([], [new("small", [left]), new("large", [right])], null, query);

        Assert.Contains($"has a different {field} than", exception.Message, StringComparison.Ordinal);
        AssertSafe(exception, left, right, "secret-aa", "secret-bb", "12345.6700", "12345.67");
    }

    [Fact]
    public void Prepare_treats_bare_decimal_and_numeric_as_the_same_type()
    {
        var sets = MeasureParameterSetValidator.Prepare(
            [],
            [new("small", ["amount:decimal=1.50"]), new("large", ["amount:numeric=2.5"])],
            null,
            "select @amount");

        Assert.Equal([new MeasureParameterMetadata("@amount", "decimal")], sets[0].Metadata);
        Assert.Equal(sets[0].Metadata, sets[1].Metadata);
        Assert.NotEqual(sets[0].ValueHash, sets[1].ValueHash);
    }

    [Fact]
    public void Prepare_rejects_different_precision_across_sets()
    {
        var exception = Reject(
            [],
            [new("small", ["amount:decimal(10,2)=12345.67"]), new("large", ["amount:decimal(12,2)=12345.67"])],
            null,
            "select @amount");

        Assert.Equal(
            "Parameter set 'large' SQL parameter '@amount' has a different precision than parameter set 'small'.",
            exception.Message);
        AssertSafe(exception, "12345.67", "decimal(10,2)", "decimal(12,2)", "amount:decimal");
    }

    [Fact]
    public void Prepare_rejects_different_scale_across_sets()
    {
        var exception = Reject(
            [],
            [new("small", ["amount:decimal(12,2)=12345.67"]), new("large", ["amount:decimal(12,4)=12345.6700"])],
            null,
            "select @amount");

        Assert.Equal(
            "Parameter set 'large' SQL parameter '@amount' has a different scale than parameter set 'small'.",
            exception.Message);
        AssertSafe(exception, "12345.67", "12345.6700", "decimal(12,2)", "decimal(12,4)", "amount:decimal");
    }

    [Fact]
    public void Prepare_rejects_unreferenced_parameters()
    {
        var exception = Reject(
            [$"unused:nvarchar(30)={Secret}"],
            [new("small", ["id:int=717171"]), new("large", ["id:int=727272"])],
            null,
            "select @id");

        Assert.Equal("SQL parameter '@unused' is not referenced by the applicable batch.", exception.Message);
        AssertSafe(exception, Secret, "717171", "727272", "unused:nvarchar(30)=");
    }

    [Fact]
    public void Prepare_rejects_malformed_sql_without_parameter_text()
    {
        var exception = Reject(
            [],
            [new("small", ["id:int=717171"]), new("large", ["id:int=727272"])],
            $"select @id as [{Secret}",
            "select @id");

        Assert.Equal("SQL parameter references could not be parsed.", exception.Message);
        AssertSafe(exception, Secret, "717171", "727272");
    }

    [Fact]
    public void Prepare_rejects_invalid_values_without_echoing_them()
    {
        var exception = Reject(
            [],
            [new("small", ["id:int=1"]), new("large", ["id:int=not-a-number-secret"])],
            null,
            "select @id");

        Assert.Equal("Parameter set 'large' is invalid: Invalid value for SQL parameter 'id'.", exception.Message);
        AssertSafe(exception, "not-a-number-secret", "id:int=not-a-number-secret");
    }

    [Fact]
    public void Prepare_accepts_references_in_setup_or_query_ignoring_case()
    {
        var sets = MeasureParameterSetValidator.Prepare(
            ["Seed:int=5"],
            [new("small", ["ItemId:int=1"]), new("large", ["itemid:int=2"])],
            "select @SEED",
            "select @ITEMID");

        Assert.Equal(["@ItemId", "@Seed"], sets[0].Metadata.Select(metadata => metadata.Name));
        Assert.Equal(["@itemid", "@Seed"], sets[1].Metadata.Select(metadata => metadata.Name));
        Assert.Equal(["int", "int"], sets[0].Metadata.Select(metadata => metadata.Type));
        Assert.NotEqual(sets[0].ValueHash, sets[1].ValueHash);
    }

    [Fact]
    public void Prepare_hashes_are_independent_of_input_order_and_culture()
    {
        var left = HashUnder("en-US", ["b:int=2", "Amount:decimal(19,4)=1234.5600"], ["d:datetime2=2026-07-29T12:00:00", "c:float=1.5"]);
        var reversed = HashUnder("pl-PL", ["Amount:decimal(19,4)=1234.5600", "b:int=2"], ["c:float=1.5", "d:datetime2=2026-07-29T12:00:00"]);
        var turkish = HashUnder("tr-TR", ["b:int=2", "amount:decimal(19,4)=1234.5600"], ["D:datetime2=2026-07-29T12:00:00", "C:float=1.5"]);

        Assert.Equal(left, reversed);
        Assert.Equal(left, turkish);
    }

    [Fact]
    public void Prepare_includes_fixed_values_in_the_hash()
    {
        var first = MeasureParameterSetValidator.Prepare(
            ["tenant:nvarchar(20)=acme"],
            [new("small", ["id:int=1"]), new("large", ["id:int=2"])],
            null,
            "select @tenant, @id");
        var second = MeasureParameterSetValidator.Prepare(
            ["tenant:nvarchar(20)=other"],
            [new("small", ["id:int=1"]), new("large", ["id:int=2"])],
            null,
            "select @tenant, @id");

        Assert.NotEqual(first[0].ValueHash, second[0].ValueHash);
        Assert.Equal(first[0].Metadata, second[0].Metadata);
    }

    [Fact]
    public void Prepare_accepts_matching_nulls_and_hashes_them_apart_from_values()
    {
        var sets = MeasureParameterSetValidator.Prepare(
            ["tenant:nvarchar(20):null"],
            [
                new("empty", ["id:int:null", "amount:decimal(19,4):null"]),
                new("filled", ["id:int=0", "amount:decimal(19,4)=0"]),
            ],
            null,
            "select @tenant, @id, @amount");

        Assert.Equal(sets[0].Metadata, sets[1].Metadata);
        Assert.Equal(
            [
                new MeasureParameterMetadata("@amount", "decimal"),
                new MeasureParameterMetadata("@id", "int"),
                new MeasureParameterMetadata("@tenant", "nvarchar"),
            ],
            sets[0].Metadata);
        Assert.NotEqual(sets[0].ValueHash, sets[1].ValueHash);
        Assert.Same(DBNull.Value, sets[0].Parameters.Single(parameter => parameter.Name == "@id").Value);
    }

    [Fact]
    public void Prepare_accepts_every_shared_parameter_type_with_one_shape()
    {
        var first = SharedCases.Select(item => $"{item.Name}:{item.Declaration}={item.ValueA}").ToArray();
        var second = SharedCases.Select(item => $"{item.Name}:{item.Declaration}={item.ValueB}").ToArray();
        var query = "select " + string.Join(", ", SharedCases.Select(item => "@" + item.Name));

        IReadOnlyList<PreparedMeasureParameterSet> Run() =>
            MeasureParameterSetValidator.Prepare([], [new("left", first), new("right", second)], null, query);

        var invariant = Run();
        IReadOnlyList<PreparedMeasureParameterSet> polish;
        IReadOnlyList<PreparedMeasureParameterSet> turkish;
        using (new TemporaryCulture("pl-PL"))
            polish = Run();
        using (new TemporaryCulture("tr-TR"))
            turkish = Run();

        Assert.Equal(invariant[0].ValueHash, polish[0].ValueHash);
        Assert.Equal(invariant[1].ValueHash, turkish[1].ValueHash);
        Assert.NotEqual(invariant[0].ValueHash, invariant[1].ValueHash);
        Assert.Equal(invariant[0].Metadata, invariant[1].Metadata);
        Assert.Equal(
            SharedCases.Select(item => "@" + item.Name).OrderBy(name => name.ToLowerInvariant(), StringComparer.Ordinal),
            invariant[0].Metadata.Select(metadata => metadata.Name));
        Assert.Equal(
            SharedCases
                .OrderBy(item => item.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .Select(item => item.MetadataType),
            invariant[0].Metadata.Select(metadata => metadata.Type));

        var metadata = JsonSerializer.Serialize(invariant[0].Metadata);
        using (var metadataJson = JsonDocument.Parse(metadata))
        {
            foreach (var element in metadataJson.RootElement.EnumerateArray())
            {
                Assert.Equal(
                    ["Name", "Type"],
                    element.EnumerateObject().Select(property => property.Name).ToArray());
            }
        }

        var safe = JsonSerializer.Serialize(invariant.Select(set => new { set.Name, set.Metadata, set.ValueHash }));
        foreach (var value in SharedCases.SelectMany(item => new[] { item.ValueA, item.ValueB }))
        {
            if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '@'))
                Assert.DoesNotContain(value, safe, StringComparison.Ordinal);
        }
    }

    private static string HashUnder(string culture, string[] fixedParameters, string[] setParameters)
    {
        using var _ = new TemporaryCulture(culture);
        var sets = MeasureParameterSetValidator.Prepare(
            fixedParameters,
            [new("only", setParameters)],
            null,
            "select @b, @Amount, @c, @d");
        return Assert.Single(sets).ValueHash;
    }

    private static SqlHarnessSafetyException Reject(
        IReadOnlyList<string> fixedParameters,
        IReadOnlyList<SqlHarnessParameterSetInput> sets,
        string? setup,
        string query) =>
        Assert.Throws<SqlHarnessSafetyException>(() =>
            MeasureParameterSetValidator.Prepare(fixedParameters, sets, setup, query));

    private static void AssertSafe(Exception exception, params string[] secrets)
    {
        var text = exception.ToString();
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    private static readonly SharedCase[] SharedCases =
    [
        new("pNvarchar", "nvarchar(20)", "alpha", "beta", "nvarchar"),
        new("pNvarcharMax", "nvarchar(max)", "alpha", "alphabet", "nvarchar"),
        new("pVarchar", "varchar(20)", "alpha", "beta", "varchar"),
        new("pVarcharMax", "varchar(max)", "alpha", "alphabet", "varchar"),
        new("pChar", "char(4)", "abcd", "abce", "char"),
        new("pNChar", "nchar(4)", "abcd", "abce", "nchar"),
        new("pInt", "int", "1", "2", "int"),
        new("pBigint", "bigint", "11", "22", "bigint"),
        new("pSmallint", "smallint", "3", "4", "smallint"),
        new("pTinyint", "tinyint", "5", "6", "tinyint"),
        new("pBit", "bit", "false", "true", "bit"),
        new("pDecimal", "decimal", "1.5", "2.5", "decimal"),
        new("pDecimalScaled", "decimal(19,4)", "1.5000", "2.5000", "decimal"),
        new("pNumeric", "numeric", "3.5", "4.5", "decimal"),
        new("pNumericScaled", "numeric(10,2)", "1.25", "2.50", "decimal"),
        new("pFloat", "float", "1.5", "2.5", "float"),
        new("pReal", "real", "1.25", "2.25", "real"),
        new("pMoney", "money", "19.99", "20.01", "money"),
        new("pSmallmoney", "smallmoney", "1.25", "2.25", "smallmoney"),
        new("pDate", "date", "2026-07-29", "2026-07-30", "date"),
        new("pTime", "time", "14:30:00", "14:31:00", "time"),
        new("pDatetime", "datetime", "2026-07-29T12:00:00", "2026-07-29T12:00:01", "datetime"),
        new("pDatetime2", "datetime2", "2026-07-29T12:00:00.0000001", "2026-07-29T12:00:00.0000002", "datetime2"),
        new("pSmalldatetime", "smalldatetime", "2026-07-29T12:00:00", "2026-07-29T12:01:00", "smalldatetime"),
        new("pDatetimeoffset", "datetimeoffset", "2026-07-29T12:00:00+02:00", "2026-07-29T12:00:00+00:00", "datetimeoffset"),
        new("pGuid", "uniqueidentifier", "0f8fad5b-d9cb-469f-a165-70867728950e", "11111111-1111-1111-1111-111111111111", "uniqueidentifier"),
        new("pVarbinary", "varbinary", "AQID", "BAUG", "varbinary"),
        new("pVarbinaryMax", "varbinary(max)", "AQID", "BAUGBA==", "varbinary"),
        new("pBinary", "binary(2)", "AQI=", "AgM=", "binary"),
        new("pHierarchy", "hierarchyid", "/1/", "/2/", "hierarchyid"),
        new("pGeography", "geography", "4326;POINT(-122.3 47.6)", "4326;POINT(-122.4 47.6)", "geography"),
        new("pGeometry", "geometry", "POINT(1 2)", "POINT(3 4)", "geometry"),
    ];

    private sealed record SharedCase(string Name, string Declaration, string ValueA, string ValueB, string MetadataType);

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