using System.Security.Cryptography;
using System.Text.Json;

namespace SqlHarness.Core;

public enum ResultComparisonMode
{
    Ordered,
    Multiset,
    Set,
    Off,
}

public sealed record ResultEquivalenceReport(
    ResultComparisonMode Mode,
    bool? Equivalent,
    long? DifferingPositions,
    long? BaselineOnlyCount,
    long? CandidateOnlyCount);

internal sealed record CanonicalComparisonResult(string SchemaHash, IReadOnlyList<string> OrderedRows);

internal static class ResultComparer
{
    public static ResultEquivalenceReport Compare(
        ResultComparisonMode mode,
        IReadOnlyList<CanonicalComparisonResult> baseline,
        IReadOnlyList<CanonicalComparisonResult> candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (mode == ResultComparisonMode.Off)
            return new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null);

        if (baseline.Count == 0)
            throw new ArgumentException("At least one baseline measured result is required.", nameof(baseline));

        var reference = baseline[0];
        var equivalent = true;
        foreach (var run in baseline)
        {
            if (!IsEquivalent(mode, reference, run))
                equivalent = false;
        }

        foreach (var run in candidate)
        {
            if (!IsEquivalent(mode, reference, run))
                equivalent = false;
        }

        long? maxDifferingPositions = mode == ResultComparisonMode.Ordered ? 0 : null;
        long maxBaselineOnly = 0;
        long maxCandidateOnly = 0;

        foreach (var left in baseline)
        {
            foreach (var right in candidate)
            {
                var counts = CountPair(mode, left, right);
                if (maxDifferingPositions is not null)
                    maxDifferingPositions = Math.Max(maxDifferingPositions.Value, counts.DifferingPositions);
                maxBaselineOnly = Math.Max(maxBaselineOnly, counts.BaselineOnly);
                maxCandidateOnly = Math.Max(maxCandidateOnly, counts.CandidateOnly);
            }
        }

        return new ResultEquivalenceReport(
            mode,
            equivalent,
            maxDifferingPositions,
            maxBaselineOnly,
            maxCandidateOnly);
    }

    private static bool IsEquivalent(
        ResultComparisonMode mode,
        CanonicalComparisonResult left,
        CanonicalComparisonResult right)
    {
        if (!string.Equals(left.SchemaHash, right.SchemaHash, StringComparison.Ordinal))
            return false;

        return mode switch
        {
            ResultComparisonMode.Ordered => left.OrderedRows.SequenceEqual(right.OrderedRows, StringComparer.Ordinal),
            ResultComparisonMode.Multiset => MultisetEqual(left.OrderedRows, right.OrderedRows),
            ResultComparisonMode.Set => SetEqual(left.OrderedRows, right.OrderedRows),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported result comparison mode."),
        };
    }

    private static (long DifferingPositions, long BaselineOnly, long CandidateOnly) CountPair(
        ResultComparisonMode mode,
        CanonicalComparisonResult left,
        CanonicalComparisonResult right)
    {
        // Directional multiset counts are always available for diagnostics under ordered/multiset/set.
        // Ordered additionally reports positional differences. Schema mismatch does not zero row-level counts.
        var (baselineOnly, candidateOnly) = mode == ResultComparisonMode.Set
            ? SetDirectionalCounts(left.OrderedRows, right.OrderedRows)
            : MultisetDirectionalCounts(left.OrderedRows, right.OrderedRows);

        var differingPositions = mode == ResultComparisonMode.Ordered
            ? CountDifferingPositions(left.OrderedRows, right.OrderedRows)
            : 0;

        return (differingPositions, baselineOnly, candidateOnly);
    }

    private static long CountDifferingPositions(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var shared = Math.Min(left.Count, right.Count);
        long differing = 0;
        for (var index = 0; index < shared; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                differing++;
        }

        differing += Math.Abs(left.Count - right.Count);
        return differing;
    }

    private static bool MultisetEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        var (baselineOnly, candidateOnly) = MultisetDirectionalCounts(left, right);
        return baselineOnly == 0 && candidateOnly == 0;
    }

    private static (long BaselineOnly, long CandidateOnly) MultisetDirectionalCounts(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var frequencies = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var fingerprint in left)
        {
            frequencies.TryGetValue(fingerprint, out var count);
            frequencies[fingerprint] = count + 1;
        }

        foreach (var fingerprint in right)
        {
            frequencies.TryGetValue(fingerprint, out var count);
            frequencies[fingerprint] = count - 1;
        }

        long baselineOnly = 0;
        long candidateOnly = 0;
        foreach (var count in frequencies.Values)
        {
            if (count > 0)
                baselineOnly += count;
            else if (count < 0)
                candidateOnly += -count;
        }

        return (baselineOnly, candidateOnly);
    }

    private static bool SetEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);
        return leftSet.SetEquals(rightSet);
    }

    private static (long BaselineOnly, long CandidateOnly) SetDirectionalCounts(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);
        var baselineOnly = leftSet.Count(fingerprint => !rightSet.Contains(fingerprint));
        var candidateOnly = rightSet.Count(fingerprint => !leftSet.Contains(fingerprint));
        return (baselineOnly, candidateOnly);
    }
}

internal sealed class CanonicalComparisonAccumulator : IDisposable
{
    internal const int MaximumComparedRows = 1_000_000;
    private const string RowLimitMessage = "Result comparison exceeds the 1000000-row limit.";

    private readonly int _maximumRows;
    private readonly IncrementalHash _schemaHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<string> _orderedRows = [];
    private int _resultSetOrdinal = -1;
    private int _activeResultSetOrdinal = -1;
    private int _rowCount;
    private bool _inResultSet;
    private bool _completed;

    public CanonicalComparisonAccumulator()
        : this(MaximumComparedRows)
    {
    }

    internal CanonicalComparisonAccumulator(int maximumRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRows);
        _maximumRows = maximumRows;
    }

    public void BeginResultSet(IReadOnlyList<CanonicalColumn> columns)
    {
        EnsureActive();
        if (_inResultSet)
            throw new InvalidOperationException("The current result set must end before another begins.");

        ArgumentNullException.ThrowIfNull(columns);
        _activeResultSetOrdinal = ++_resultSetOrdinal;
        AppendSchema(_activeResultSetOrdinal, columns);
        _inResultSet = true;
    }

    public void AddRow(IReadOnlyList<object?> values)
    {
        EnsureActive();
        if (!_inResultSet)
            throw new InvalidOperationException("A result set must begin before rows are added.");

        ArgumentNullException.ThrowIfNull(values);
        var preparedValues = values.Select(CanonicalScalarCodec.Prepare).ToArray();

        _rowCount++;
        if (_rowCount > _maximumRows)
            throw new SqlHarnessSafetyException(RowLimitMessage);

        _orderedRows.Add(HashRow(_activeResultSetOrdinal, preparedValues));
    }

    public void EndResultSet()
    {
        EnsureActive();
        if (!_inResultSet)
            throw new InvalidOperationException("No result set is active.");

        _inResultSet = false;
        _activeResultSetOrdinal = -1;
    }

    public CanonicalComparisonResult Complete()
    {
        EnsureActive();
        if (_inResultSet)
            throw new InvalidOperationException("The current result set must end before completion.");

        _completed = true;
        var schemaHash = Convert.ToHexString(_schemaHash.GetHashAndReset());
        return new CanonicalComparisonResult(schemaHash, _orderedRows.ToArray());
    }

    public void Dispose() => _schemaHash.Dispose();

    private void AppendSchema(int resultSetOrdinal, IReadOnlyList<CanonicalColumn> columns)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("resultSet", resultSetOrdinal);
            writer.WritePropertyName("columns");
            writer.WriteStartArray();
            foreach (var column in columns)
            {
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", column.Ordinal);
                writer.WriteString("name", column.Name);
                writer.WriteString("dataType", column.DataType);
                writer.WriteBoolean("allowNull", column.AllowNull);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        _schemaHash.AppendData(stream.ToArray());
    }

    private static string HashRow(int resultSetOrdinal, CanonicalScalarCodec.PreparedScalar[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("resultSet", resultSetOrdinal);
            writer.WritePropertyName("values");
            writer.WriteStartArray();
            foreach (var value in values)
                CanonicalScalarCodec.Write(writer, value);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private void EnsureActive()
    {
        if (_completed)
            throw new InvalidOperationException("The canonical comparison result is already complete.");
    }
}
