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

internal sealed record CanonicalComparisonResult(string SchemaHash, IReadOnlyList<string> OrderedRows);

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
