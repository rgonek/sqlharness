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

/// <summary>
/// Cell-wide budget for stored unique comparison fingerprints (006 resource contract).
/// Counts UNIQUE stored fingerprints per compare cell; shared representations of
/// identical runs count once, so deduplication is rewarded, not penalized.
/// </summary>
internal static class ComparisonBudget
{
    internal const int DefaultMaxUniqueFingerprints = 2_000_000;
}

internal static class ResultComparer
{
    private const int TokenCheckStride = 4096;

    public static ResultEquivalenceReport Compare(
        ResultComparisonMode mode,
        IReadOnlyList<CanonicalComparisonResult> baseline,
        IReadOnlyList<CanonicalComparisonResult> candidate,
        CancellationToken cancellationToken = default)
    {
        return Compare(mode, baseline, candidate, ComparisonBudget.DefaultMaxUniqueFingerprints, cancellationToken);
    }

    internal static ResultEquivalenceReport Compare(
        ResultComparisonMode mode,
        IReadOnlyList<CanonicalComparisonResult> baseline,
        IReadOnlyList<CanonicalComparisonResult> candidate,
        int maxUniqueFingerprints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (mode == ResultComparisonMode.Off)
            return new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null);

        if (baseline.Count == 0)
            throw new ArgumentException("At least one baseline measured result is required.", nameof(baseline));

        // Deduplicate runs by content key so CPU grows with UNIQUE results, not runs x pairs.
        var registry = new SharedRepresentationRegistry(mode, maxUniqueFingerprints, cancellationToken);
        var baselineReps = new SharedRepresentation[baseline.Count];
        for (var index = 0; index < baseline.Count; index++)
            baselineReps[index] = registry.GetOrAdd(baseline[index]);
        var candidateReps = new SharedRepresentation[candidate.Count];
        for (var index = 0; index < candidate.Count; index++)
            candidateReps[index] = registry.GetOrAdd(candidate[index]);

        var reference = baselineReps[0];
        var equivalent = true;
        foreach (var run in baselineReps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reference.IsEquivalentTo(run, mode))
                equivalent = false;
        }

        foreach (var run in candidateReps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reference.IsEquivalentTo(run, mode))
                equivalent = false;
        }

        long? maxDifferingPositions = mode == ResultComparisonMode.Ordered ? 0 : null;
        long maxBaselineOnly = 0;
        long maxCandidateOnly = 0;

        foreach (var left in baselineReps)
        {
            foreach (var right in candidateReps)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    private static (long DifferingPositions, long BaselineOnly, long CandidateOnly) CountPair(
        ResultComparisonMode mode,
        SharedRepresentation left,
        SharedRepresentation right)
    {
        // Directional multiset counts are always available for diagnostics under ordered/multiset/set.
        // Ordered additionally reports positional differences. Schema mismatch does not zero row-level counts.
        var (baselineOnly, candidateOnly) = mode == ResultComparisonMode.Set
            ? left.SetDirectionalCounts(right)
            : left.MultisetDirectionalCounts(right);

        var differingPositions = mode == ResultComparisonMode.Ordered
            ? CountDifferingPositions(left.Rows, right.Rows)
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

    /// <summary>
    /// One cached representation per unique measured result. Ordered compares the original
    /// row list without copying; multiset/set share one histogram plus a lazily built,
    /// once-cached set for set semantics. The ordered histogram is built lazily on first
    /// use for directional counts and cached the same way.
    /// </summary>
    private sealed class SharedRepresentation
    {
        private readonly CancellationToken _cancellationToken;
        private Dictionary<string, long>? _histogram;
        private HashSet<string>? _set;

        internal string SchemaHash { get; }
        internal IReadOnlyList<string> Rows { get; }

        internal SharedRepresentation(
            CanonicalComparisonResult result,
            bool buildHistogramEagerly,
            CancellationToken cancellationToken)
        {
            SchemaHash = result.SchemaHash;
            Rows = result.OrderedRows;
            _cancellationToken = cancellationToken;
            if (buildHistogramEagerly)
                _histogram = BuildHistogram(Rows, cancellationToken);
        }

        internal bool IsEquivalentTo(SharedRepresentation other, ResultComparisonMode mode)
        {
            if (!string.Equals(SchemaHash, other.SchemaHash, StringComparison.Ordinal))
                return false;

            return mode switch
            {
                ResultComparisonMode.Ordered => Rows.SequenceEqual(other.Rows, StringComparer.Ordinal),
                ResultComparisonMode.Multiset => HistogramsEqual(GetHistogram(), other.GetHistogram()),
                ResultComparisonMode.Set => GetSet().SetEquals(other.GetSet()),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported result comparison mode."),
            };
        }

        internal (long BaselineOnly, long CandidateOnly) MultisetDirectionalCounts(SharedRepresentation other)
        {
            var left = GetHistogram();
            var right = other.GetHistogram();
            long baselineOnly = 0;
            long candidateOnly = 0;
            foreach (var (fingerprint, count) in left)
            {
                right.TryGetValue(fingerprint, out var rightCount);
                if (count > rightCount)
                    baselineOnly += count - rightCount;
            }

            foreach (var (fingerprint, count) in right)
            {
                left.TryGetValue(fingerprint, out var leftCount);
                if (count > leftCount)
                    candidateOnly += count - leftCount;
            }

            return (baselineOnly, candidateOnly);
        }

        internal (long BaselineOnly, long CandidateOnly) SetDirectionalCounts(SharedRepresentation other)
        {
            var left = GetSet();
            var right = other.GetSet();
            var baselineOnly = left.Count(fingerprint => !right.Contains(fingerprint));
            var candidateOnly = right.Count(fingerprint => !left.Contains(fingerprint));
            return (baselineOnly, candidateOnly);
        }

        internal Dictionary<string, long> GetHistogram() =>
            _histogram ??= BuildHistogram(Rows, _cancellationToken);

        internal HashSet<string> GetSet()
        {
            if (_set is not null)
                return _set;
            if (_histogram is not null)
            {
                _set = new HashSet<string>(_histogram.Keys, StringComparer.Ordinal);
                return _set;
            }

            _set = new HashSet<string>(Rows, StringComparer.Ordinal);
            return _set;
        }

        private static Dictionary<string, long> BuildHistogram(IReadOnlyList<string> rows, CancellationToken cancellationToken)
        {
            var frequencies = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var index = 0; index < rows.Count; index++)
            {
                if ((index & (TokenCheckStride - 1)) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                frequencies.TryGetValue(rows[index], out var count);
                frequencies[rows[index]] = count + 1;
            }

            return frequencies;
        }

        private static bool HistogramsEqual(Dictionary<string, long> left, Dictionary<string, long> right)
        {
            if (left.Count != right.Count)
                return false;
            foreach (var (fingerprint, count) in left)
            {
                if (!right.TryGetValue(fingerprint, out var other) || other != count)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Content-keyed registry of shared representations. The key is
    /// (SchemaHash, row count, HashCode over OrderedRows); a key hit is verified
    /// with a full SequenceEqual before sharing so hash collisions never share.
    /// Each unique representation registers its row count against the cell budget;
    /// histogram entries fit in the same counter (at most one entry per unique
    /// fingerprint). Exceeding the budget throws SqlHarnessSafetyException (fail
    /// closed: never truncates, never reports Equivalent on partial data).
    /// </summary>
    private sealed class SharedRepresentationRegistry
    {
        private readonly bool _buildHistogramsEagerly;
        private readonly int _maxUniqueFingerprints;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<(string SchemaHash, int RowCount, int ContentHash), List<SharedRepresentation>> _entries = new();
        private long _uniqueFingerprintsUsed;

        internal SharedRepresentationRegistry(
            ResultComparisonMode mode,
            int maxUniqueFingerprints,
            CancellationToken cancellationToken)
        {
            _buildHistogramsEagerly = mode is ResultComparisonMode.Multiset or ResultComparisonMode.Set;
            _maxUniqueFingerprints = maxUniqueFingerprints;
            _cancellationToken = cancellationToken;
        }

        internal SharedRepresentation GetOrAdd(CanonicalComparisonResult result)
        {
            var key = ComputeContentKey(result);
            if (_entries.TryGetValue(key, out var bucket))
            {
                foreach (var existing in bucket)
                {
                    if (string.Equals(existing.SchemaHash, result.SchemaHash, StringComparison.Ordinal)
                        && existing.Rows.Count == result.OrderedRows.Count
                        && existing.Rows.SequenceEqual(result.OrderedRows, StringComparer.Ordinal))
                    {
                        return existing;
                    }
                }
            }
            else
            {
                bucket = [];
                _entries[key] = bucket;
            }

            _uniqueFingerprintsUsed += result.OrderedRows.Count;
            if (_uniqueFingerprintsUsed > _maxUniqueFingerprints)
            {
                throw new SqlHarnessSafetyException(
                    $"Result comparison exceeds the comparison budget ({_uniqueFingerprintsUsed} unique fingerprints used, limit {_maxUniqueFingerprints}).");
            }

            var representation = new SharedRepresentation(result, _buildHistogramsEagerly, _cancellationToken);
            bucket.Add(representation);
            return representation;
        }

        private (string SchemaHash, int RowCount, int ContentHash) ComputeContentKey(CanonicalComparisonResult result)
        {
            var hash = new HashCode();
            hash.Add(result.SchemaHash, StringComparer.Ordinal);
            hash.Add(result.OrderedRows.Count);
            for (var index = 0; index < result.OrderedRows.Count; index++)
            {
                if ((index & (TokenCheckStride - 1)) == 0)
                    _cancellationToken.ThrowIfCancellationRequested();
                hash.Add(result.OrderedRows[index], StringComparer.Ordinal);
            }

            return (result.SchemaHash, result.OrderedRows.Count, hash.ToHashCode());
        }
    }
}

internal sealed class CanonicalComparisonAccumulator : IDisposable
{
    /// <summary>
    /// Fails closed: exceeding this bound throws instead of truncating, so snapshots
    /// keep rejecting incomplete data and equivalence still covers required results.
    /// Presentation caps (--max-rows, agent budgets) never feed this path.
    /// </summary>
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