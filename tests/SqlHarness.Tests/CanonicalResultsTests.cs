using System.Data.SqlTypes;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.SqlServer.Types;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class CanonicalResultsTests
{
    [Fact]
    public void Same_values_in_different_row_order_are_not_equivalent()
    {
        var a = CanonicalFixture.ResultSet([1, 2]);
        var b = CanonicalFixture.ResultSet([2, 1]);

        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Integer_and_text_with_the_same_display_value_are_not_equivalent()
    {
        var integer = CanonicalFixture.ResultSet([1]);
        var text = CanonicalFixture.ResultSet(["1"]);

        Assert.NotEqual(integer.Hash, text.Hash);
    }

    [Fact]
    public void Null_has_an_explicit_marker_distinct_from_empty_text()
    {
        var nullValue = CanonicalFixture.ResultSet([null]);
        var emptyText = CanonicalFixture.ResultSet([string.Empty]);

        Assert.NotEqual(nullValue.Hash, emptyText.Hash);
    }

    [Fact]
    public void Result_set_boundaries_affect_the_hash()
    {
        var oneResultSet = CanonicalFixture.ResultSets([1, 2]);
        var twoResultSets = CanonicalFixture.ResultSets([1], [2]);

        Assert.NotEqual(oneResultSet.Hash, twoResultSets.Hash);
    }

    [Fact]
    public void Column_metadata_affects_the_hash()
    {
        var original = CanonicalFixture.ResultSet([1], new CanonicalColumn(0, "Value", "System.Int32", false));
        var renamed = CanonicalFixture.ResultSet([1], new CanonicalColumn(0, "Other", "System.Int32", false));
        var retyped = CanonicalFixture.ResultSet([1], new CanonicalColumn(0, "Value", "System.Int64", false));
        var nullable = CanonicalFixture.ResultSet([1], new CanonicalColumn(0, "Value", "System.Int32", true));

        Assert.NotEqual(original.Hash, renamed.Hash);
        Assert.NotEqual(original.Hash, retyped.Hash);
        Assert.NotEqual(original.Hash, nullable.Hash);
    }

    [Fact]
    public void Identical_results_have_identical_hashes_and_footprints()
    {
        var first = CanonicalFixture.ResultSet([1, null, "Zażółć"]);
        var second = CanonicalFixture.ResultSet([1, null, "Zażółć"]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Canonical_property_order_and_raw_counts_are_stable()
    {
        const string expected = "{\"events\":[{\"kind\":\"resultSetStart\",\"columns\":[{\"ordinal\":0,\"name\":\"Value\",\"dataType\":\"System.Int32\",\"allowNull\":false}]},{\"kind\":\"row\",\"values\":[{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":1}]},{\"kind\":\"resultSetEnd\",\"rowCount\":1}]}";
        var bytes = Encoding.UTF8.GetBytes(expected);

        var actual = CanonicalFixture.ResultSet([1]);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), actual.Hash);
        Assert.Equal(new OutputFootprint(bytes.Length, 1), actual.Footprint);
    }

    [Fact]
    public void Footprint_token_estimate_identifies_its_byte_heuristic()
    {
        Assert.Equal("utf8-bytes-div-4", OutputFootprint.EstimationMethod);
        Assert.Equal(2, OutputFootprint.EstimateTokens(5));
    }

    [Fact]
    public void Unsupported_scalar_rejection_leaves_the_accumulator_clean_and_usable()
    {
        const string expected = "{\"events\":[{\"kind\":\"resultSetStart\",\"columns\":[{\"ordinal\":0,\"name\":\"Value\",\"dataType\":\"unsupported\",\"allowNull\":false}]},{\"kind\":\"row\",\"values\":[{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":1}]},{\"kind\":\"resultSetEnd\",\"rowCount\":1}]}";
        var bytes = Encoding.UTF8.GetBytes(expected);
        using var accumulator = new CanonicalResultAccumulator();
        accumulator.BeginResultSet([new CanonicalColumn(0, "Value", "unsupported", false)]);

        var exception = Assert.Throws<NotSupportedException>(() =>
            accumulator.AddRow([new CultureDependentScalar()]));
        accumulator.AddRow([1]);
        accumulator.EndResultSet();
        var actual = accumulator.Complete();

        Assert.Contains(typeof(CultureDependentScalar).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), actual.Hash);
        Assert.Equal(new OutputFootprint(bytes.Length, 1), actual.Footprint);
    }

    [Theory]
    [MemberData(nameof(NonFiniteFloatingValues))]
    public void Non_finite_floating_scalars_have_stable_tagged_encodings(object nonFiniteValue)
    {
        var first = CanonicalFixture.ResultSet([nonFiniteValue]);
        var second = CanonicalFixture.ResultSet([nonFiniteValue]);
        var type = nonFiniteValue is float ? "singleNonFinite" : "doubleNonFinite";
        var text = nonFiniteValue switch
        {
            float value when float.IsNaN(value) => "NaN",
            double value when double.IsNaN(value) => "NaN",
            float value when float.IsPositiveInfinity(value) => "Infinity",
            double value when double.IsPositiveInfinity(value) => "Infinity",
            _ => "-Infinity",
        };

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Footprint, second.Footprint);
        Assert.Equal($"{{\"type\":\"{type}\",\"isNull\":false,\"length\":{text.Length},\"value\":\"{text}\"}}", CanonicalScalarJson(nonFiniteValue));
    }

    [Fact]
    public void Non_finite_double_nan_has_explicit_canonical_json()
    {
        const string expected = "{\"events\":[{\"kind\":\"resultSetStart\",\"columns\":[{\"ordinal\":0,\"name\":\"Value\",\"dataType\":\"floating\",\"allowNull\":false}]},{\"kind\":\"row\",\"values\":[{\"type\":\"doubleNonFinite\",\"isNull\":false,\"length\":3,\"value\":\"NaN\"}]},{\"kind\":\"resultSetEnd\",\"rowCount\":1}]}";
        var bytes = Encoding.UTF8.GetBytes(expected);

        var actual = CanonicalFixture.ResultSet(
            [double.NaN], new CanonicalColumn(0, "Value", "floating", false));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), actual.Hash);
        Assert.Equal(new OutputFootprint(bytes.Length, 1), actual.Footprint);
        Assert.NotEqual(CanonicalFixture.ResultSet([double.NaN]).Hash,
            CanonicalFixture.ResultSet([double.PositiveInfinity]).Hash);
    }

    [Fact]
    public void Sql_hierarchy_id_uses_invariant_named_encoding()
    {
        AssertProviderValue(SqlHierarchyId.Parse("/1/2/"), "hierarchyId", "/1/2/");
    }

    [Fact]
    public void Sql_geography_uses_invariant_named_encoding()
    {
        AssertProviderValue(SqlGeography.Point(1, 2, 4326), "geography", "4326;POINT (2 1)");
    }

    [Fact]
    public void Sql_geometry_uses_invariant_named_encoding()
    {
        AssertProviderValue(SqlGeometry.Point(1, 2, 4326), "geometry", "4326;POINT (1 2)");
    }

    [Fact]
    public void Sql_null_provider_values_use_the_null_envelope()
    {
        AssertProviderNull(SqlHierarchyId.Null);
        AssertProviderNull(SqlGeography.Null);
        AssertProviderNull(SqlGeometry.Null);
    }

    [Fact]
    public void Arrays_have_stable_ordered_canonical_encodings()
    {
        var values = new[] { 1, 2 };
        var first = CanonicalFixture.ResultSet([values]);
        var second = CanonicalFixture.ResultSet([new[] { 1, 2 }]);

        Assert.Equal(first.Hash, second.Hash);
        Assert.NotEqual(CanonicalFixture.ResultSet([new[] { 2, 1 }]).Hash, first.Hash);
        Assert.Equal(
            "{\"type\":\"array\",\"isNull\":false,\"length\":2,\"value\":[{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":1},{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":2}]}",
            CanonicalScalarJson(values));
    }

    [Fact]
    public void Arrays_encode_nulls_empty_arrays_and_multidimensional_shape()
    {
        Assert.Equal(
            "{\"type\":\"array\",\"isNull\":false,\"length\":2,\"value\":[{\"type\":\"string\",\"isNull\":false,\"length\":1,\"value\":\"a\"},{\"type\":\"null\",\"isNull\":true,\"length\":0,\"value\":null}]}",
            CanonicalScalarJson(new string?[] { "a", null }));
        Assert.Equal(
            "{\"type\":\"array\",\"isNull\":false,\"length\":0,\"value\":[]}",
            CanonicalScalarJson(Array.Empty<int>()));
        Assert.Equal(
            "{\"type\":\"array\",\"isNull\":false,\"length\":4,\"dimensions\":[2,2],\"value\":[{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":1},{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":2},{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":3},{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":4}]}",
            CanonicalScalarJson(new[,] { { 1, 2 }, { 3, 4 } }));
    }

    [Fact]
    public void Byte_arrays_keep_the_existing_binary_scalar_encoding()
    {
        const string expected = "{\"type\":\"bytes\",\"isNull\":false,\"length\":3,\"value\":\"AAH/\"}";

        Assert.Equal(expected, CanonicalScalarJson(new byte[] { 0, 1, 255 }));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), OneShotScalarHash(new byte[] { 0, 1, 255 }));
    }

    [Fact]
    public void Unsupported_array_element_rejection_leaves_accumulator_clean_and_usable()
    {
        const string expected = "{\"events\":[{\"kind\":\"resultSetStart\",\"columns\":[{\"ordinal\":0,\"name\":\"Value\",\"dataType\":\"array\",\"allowNull\":false}]},{\"kind\":\"row\",\"values\":[{\"type\":\"array\",\"isNull\":false,\"length\":1,\"value\":[{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":1}]}]},{\"kind\":\"resultSetEnd\",\"rowCount\":1}]}";
        using var accumulator = new CanonicalResultAccumulator();
        accumulator.BeginResultSet([new CanonicalColumn(0, "Value", "array", false)]);

        var exception = Assert.Throws<NotSupportedException>(() =>
            accumulator.AddRow([new object?[] { new object?[] { 1, new CultureDependentScalar() } }]));
        accumulator.AddRow([new object?[] { 1 }]);
        accumulator.EndResultSet();

        Assert.Contains(typeof(CultureDependentScalar).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))), accumulator.Complete().Hash);
    }

    [Fact]
    public void Postgres_network_and_bit_types_have_invariant_named_encodings()
    {
        AssertProviderValue(IPAddress.Parse("192.0.2.1"), "ipAddress", "192.0.2.1");
        AssertProviderValue(PhysicalAddress.Parse("001122AABBCC"), "macAddress", "001122AABBCC");
        AssertProviderValue(new System.Collections.BitArray([true, false, true, true]), "bits", "1011");
    }

    [Fact]
    public void Npgsql_cidr_ipnetwork_keeps_its_invariant_formattable_encoding()
    {
        const string expected = "{\"type\":\"System.Net.IPNetwork\",\"isNull\":false,\"length\":12,\"value\":\"192.0.2.0/24\"}";
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var value = IPNetwork.Parse("192.0.2.0/24");
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { "pl-PL", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(expected, CanonicalScalarJson(value));
                Assert.Equal(expectedHash, OneShotScalarHash(value));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Unsupported_sql_nullable_null_remains_rejected()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            CanonicalScalarCodec.Prepare(SqlXml.Null));

        Assert.Contains(typeof(SqlXml).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("not supported as a result column", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<object> NonFiniteFloatingValues =>
    [
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity,
        float.NaN,
        float.PositiveInfinity,
        float.NegativeInfinity,
    ];

    private static void AssertProviderValue(object value, string type, string text)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl-PL");
            var expectedValue = JsonSerializer.Serialize(text);
            var expected = $"{{\"type\":\"{type}\",\"isNull\":false,\"length\":{Encoding.UTF8.GetByteCount(text)},\"value\":{expectedValue}}}";
            Assert.Equal(expected, CanonicalScalarJson(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static void AssertProviderNull(object value) =>
        Assert.Equal("{\"type\":\"null\",\"isNull\":true,\"length\":0,\"value\":null}", CanonicalScalarJson(value));

    private static string CanonicalScalarJson(object value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            CanonicalScalarCodec.Write(writer, CanonicalScalarCodec.Prepare(value));
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void Supported_scalar_bytes_are_independent_of_current_culture()
    {
        var polish = CanonicalizeCultureSensitiveScalars("pl-PL");
        var american = CanonicalizeCultureSensitiveScalars("en-US");

        Assert.Equal(polish, american);
    }

    [Fact]
    public void Multiline_message_and_plan_xml_have_deterministic_compact_footprints()
    {
        const string expected = "{\"events\":[{\"kind\":\"message\",\"messageKind\":\"sql\",\"value\":{\"type\":\"string\",\"isNull\":false,\"length\":11,\"value\":\"line1\\nline2\"}},{\"kind\":\"message\",\"messageKind\":\"planXml\",\"value\":{\"type\":\"string\",\"isNull\":false,\"length\":14,\"value\":\"\\u003Cplan\\u003E\\n\\u003C/plan\\u003E\"}}]}";
        var bytes = Encoding.UTF8.GetBytes(expected);

        using var accumulator = new CanonicalResultAccumulator();
        accumulator.AddMessage("sql", "line1\nline2");
        accumulator.AddMessage("planXml", "<plan>\n</plan>");
        var actual = accumulator.Complete();

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), actual.Hash);
        Assert.Equal(new OutputFootprint(bytes.Length, 1), actual.Footprint);
    }

    [Fact]
    public void Snapshot_midstream_does_not_change_continued_hash_or_footprint()
    {
        using var snapshotted = new CanonicalResultAccumulator();
        using var uninterrupted = new CanonicalResultAccumulator();
        var columns = new[] { new CanonicalColumn(0, "Value", "System.Int32", false) };
        snapshotted.BeginResultSet(columns);
        uninterrupted.BeginResultSet(columns);
        snapshotted.AddRow([1]);
        uninterrupted.AddRow([1]);

        var partial = snapshotted.SnapshotFootprint();

        snapshotted.AddRow([2]);
        uninterrupted.AddRow([2]);
        snapshotted.EndResultSet();
        uninterrupted.EndResultSet();
        snapshotted.AddMessage("sql", "late message");
        uninterrupted.AddMessage("sql", "late message");
        var afterSnapshot = snapshotted.Complete();
        var expected = uninterrupted.Complete();

        Assert.True(partial.Bytes > 0);
        Assert.Equal(expected, afterSnapshot);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(64)]
    public void Chunked_string_cell_hash_matches_one_shot_for_every_split(int chunkSize)
    {
        // Size 1 splits at every boundary, including inside the surrogate pair
        // and around both lone surrogates.
        const string text = "plain \"quoted\" \\ slash \b\f\n\r\t \0\x1f <tag> & \u2028\u2029 Zażółć \U0001F600 lone-high \ud83d! lone-low \ude00! end";
        var chunks = Split(text, chunkSize).ToList();

        Assert.Equal(OneShotScalarHash(text), ChunkedCanonicalCellHash.HashString(chunks));
    }

    [Fact]
    public void Chunked_string_cell_hash_matches_for_empty_and_uneven_chunks()
    {
        Assert.Equal(OneShotScalarHash(string.Empty), ChunkedCanonicalCellHash.HashString([]));
        Assert.Equal(OneShotScalarHash(string.Empty), ChunkedCanonicalCellHash.HashString(["", ""]));

        const string text = "a\x1b[31m-colored-\U0001F336-string\nwith\tescapes\"";
        var chunks = new[] { "a\x1b", "[31m-colo", "red-\ud83c", "\udf36-string\nwith\tesc", "apes\"" };

        Assert.Equal(OneShotScalarHash(text), ChunkedCanonicalCellHash.HashString(chunks));
    }

    [Fact]
    public void Chunked_bytes_cell_hash_matches_one_shot_for_every_split()
    {
        var bytes = Enumerable.Range(0, 300).Select(index => (byte)(index * 37 + 11)).ToArray();
        foreach (var length in new[] { 0, 1, 2, 3, 4, 5, 57, 256, 300 })
        {
            var data = bytes[..length];
            foreach (var chunkSize in new[] { 1, 2, 3, 5, 64 })
            {
                var chunks = SplitBytes(data, chunkSize).ToList();
                Assert.Equal(OneShotScalarHash(data), ChunkedCanonicalCellHash.HashBytes(chunks));
            }
        }
    }

    private static byte[] OneShotScalarHash(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            CanonicalScalarCodec.Write(writer, CanonicalScalarCodec.Prepare(value));
        return SHA256.HashData(stream.ToArray());
    }

    private static IEnumerable<string> Split(string text, int size)
    {
        for (var index = 0; index < text.Length; index += size)
            yield return text.Substring(index, Math.Min(size, text.Length - index));
    }

    private static IEnumerable<byte[]> SplitBytes(byte[] data, int size)
    {
        for (var index = 0; index < data.Length; index += size)
            yield return data[index..Math.Min(index + size, data.Length)];
    }

    private static CanonicalResult CanonicalizeCultureSensitiveScalars(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

            using var accumulator = new CanonicalResultAccumulator();
            accumulator.BeginResultSet(
            [
                new CanonicalColumn(0, "Decimal", "System.Decimal", false),
                new CanonicalColumn(1, "Double", "System.Double", false),
                new CanonicalColumn(2, "DateTime", "System.DateTime", false),
                new CanonicalColumn(3, "TimeSpan", "System.TimeSpan", false),
            ]);
            accumulator.AddRow(
            [
                1234.56m,
                1234.56d,
                new DateTime(2026, 7, 13, 12, 34, 56, DateTimeKind.Utc),
                TimeSpan.FromMinutes(90.5),
            ]);
            accumulator.EndResultSet();
            return accumulator.Complete();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private sealed class CultureDependentScalar
    {
        public override string ToString() => 1234.56m.ToString();
    }

    private static class CanonicalFixture
    {
        private static readonly CanonicalColumn DefaultColumn =
            new(0, "Value", "System.Int32", false);

        public static CanonicalResult ResultSet(
            IReadOnlyList<object?> values,
            CanonicalColumn? column = null) => ResultSets((column ?? DefaultColumn, values));

        public static CanonicalResult ResultSets(params IReadOnlyList<object?>[] resultSets) =>
            ResultSets(resultSets.Select(values => (DefaultColumn, values)).ToArray());

        private static CanonicalResult ResultSets(
            params (CanonicalColumn Column, IReadOnlyList<object?> Values)[] resultSets)
        {
            using var accumulator = new CanonicalResultAccumulator();
            foreach (var (column, values) in resultSets)
            {
                accumulator.BeginResultSet([column]);
                foreach (var value in values)
                    accumulator.AddRow([value]);
                accumulator.EndResultSet();
            }

            return accumulator.Complete();
        }
    }
}