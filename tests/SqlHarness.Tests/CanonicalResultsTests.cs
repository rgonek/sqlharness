using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    public void Non_finite_floating_scalar_is_rejected_without_changing_state(object nonFiniteValue)
    {
        const string expected = "{\"events\":[{\"kind\":\"resultSetStart\",\"columns\":[{\"ordinal\":0,\"name\":\"Value\",\"dataType\":\"floating\",\"allowNull\":false}]},{\"kind\":\"row\",\"values\":[{\"type\":\"int32\",\"isNull\":false,\"length\":1,\"value\":1}]},{\"kind\":\"resultSetEnd\",\"rowCount\":1}]}";
        var bytes = Encoding.UTF8.GetBytes(expected);
        using var accumulator = new CanonicalResultAccumulator();
        accumulator.BeginResultSet([new CanonicalColumn(0, "Value", "floating", false)]);

        Assert.Throws<NotSupportedException>(() => accumulator.AddRow([nonFiniteValue]));
        accumulator.AddRow([1]);
        accumulator.EndResultSet();
        var actual = accumulator.Complete();

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), actual.Hash);
        Assert.Equal(new OutputFootprint(bytes.Length, 1), actual.Footprint);
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
