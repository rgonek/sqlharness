using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlHarness.Core;

public sealed record OutputFootprint(long Bytes, long Lines)
{
    public const string EstimationMethod = "utf8-bytes-div-4";

    public long Bytes { get; init; } = Bytes >= 0
        ? Bytes
        : throw new ArgumentOutOfRangeException(nameof(Bytes));

    public long Lines { get; init; } = Lines >= 0
        ? Lines
        : throw new ArgumentOutOfRangeException(nameof(Lines));

    public long EstimatedTokenCount => EstimateTokens(Bytes);

    public static long EstimateTokens(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return bytes / 4 + (bytes % 4 == 0 ? 0 : 1);
    }
}

internal sealed record CanonicalColumn(int Ordinal, string Name, string DataType, bool AllowNull);

internal sealed record CanonicalResult(string Hash, OutputFootprint Footprint);

internal static class CanonicalScalarCodec
{
    internal readonly record struct PreparedScalar(object? Value, string? InvariantValue);

    public static PreparedScalar Prepare(object? value) => value switch
    {
        float number when !float.IsFinite(number) => throw new NotSupportedException(
            "Non-finite System.Single values are not supported canonical scalars."),
        double number when !double.IsFinite(number) => throw new NotSupportedException(
            "Non-finite System.Double values are not supported canonical scalars."),
        null or DBNull or string or char or bool or byte[] or
            byte or sbyte or short or ushort or int or uint or long or ulong or
            decimal or float or double or Guid or DateTime or DateTimeOffset or
            DateOnly or TimeOnly or TimeSpan => new PreparedScalar(value, null),
        IFormattable formattable => new PreparedScalar(
            value,
            formattable.ToString(null, CultureInfo.InvariantCulture)
                ?? throw new NotSupportedException(
                    $"Canonical scalar type '{value.GetType().FullName}' returned no invariant value.")),
        _ => throw new NotSupportedException(
            $"Canonical scalar type '{value.GetType().FullName}' is not supported."),
    };

    public static void Write(Utf8JsonWriter writer, PreparedScalar scalar)
    {
        var value = scalar.Value;
        writer.WriteStartObject();
        if (value is null or DBNull)
        {
            WriteScalarHeader(writer, "null", true, 0);
            writer.WriteNull("value");
            writer.WriteEndObject();
            return;
        }

        switch (value)
        {
            case string text:
                WriteScalarHeader(writer, "string", false, Utf8Length(text));
                writer.WriteString("value", text);
                break;
            case char character:
                var characterText = character.ToString();
                WriteScalarHeader(writer, "char", false, Utf8Length(characterText));
                writer.WriteString("value", characterText);
                break;
            case bool boolean:
                WriteScalarHeader(writer, "boolean", false, boolean ? 4 : 5);
                writer.WriteBoolean("value", boolean);
                break;
            case byte number:
                WriteInteger(writer, "byte", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case sbyte number:
                WriteInteger(writer, "sbyte", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case short number:
                WriteInteger(writer, "int16", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case ushort number:
                WriteInteger(writer, "uint16", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case int number:
                WriteInteger(writer, "int32", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case uint number:
                WriteInteger(writer, "uint32", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case long number:
                WriteInteger(writer, "int64", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case ulong number:
                WriteInteger(writer, "uint64", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case decimal number:
                var decimalText = number.ToString(CultureInfo.InvariantCulture);
                WriteScalarHeader(writer, "decimal", false, Utf8Length(decimalText));
                writer.WriteNumber("value", number);
                break;
            case float number when float.IsFinite(number):
                var singleText = number.ToString("R", CultureInfo.InvariantCulture);
                WriteScalarHeader(writer, "single", false, Utf8Length(singleText));
                writer.WriteNumber("value", number);
                break;
            case double number when double.IsFinite(number):
                var doubleText = number.ToString("R", CultureInfo.InvariantCulture);
                WriteScalarHeader(writer, "double", false, Utf8Length(doubleText));
                writer.WriteNumber("value", number);
                break;
            case byte[] bytes:
                var base64 = Convert.ToBase64String(bytes);
                WriteScalarHeader(writer, "bytes", false, bytes.LongLength);
                writer.WriteString("value", base64);
                break;
            case Guid guid:
                WriteInvariantText(writer, "guid", guid.ToString("D"));
                break;
            case DateTime dateTime:
                WriteInvariantText(writer, "dateTime", dateTime.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dateTimeOffset:
                WriteInvariantText(writer, "dateTimeOffset", dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                WriteInvariantText(writer, "date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                WriteInvariantText(writer, "time", time.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                WriteInvariantText(writer, "timeSpan", timeSpan.ToString("c", CultureInfo.InvariantCulture));
                break;
            case IFormattable formattable:
                WriteInvariantText(
                    writer,
                    formattable.GetType().FullName ?? formattable.GetType().Name,
                    scalar.InvariantValue!);
                break;
            default:
                throw new NotSupportedException(
                    $"Canonical scalar type '{value.GetType().FullName}' is not supported.");
        }

        writer.WriteEndObject();
    }

    private static void WriteInteger(Utf8JsonWriter writer, string type, long value, string invariant)
    {
        WriteScalarHeader(writer, type, false, Utf8Length(invariant));
        writer.WriteNumber("value", value);
    }

    private static void WriteInteger(Utf8JsonWriter writer, string type, ulong value, string invariant)
    {
        WriteScalarHeader(writer, type, false, Utf8Length(invariant));
        writer.WriteNumber("value", value);
    }

    private static void WriteInvariantText(Utf8JsonWriter writer, string type, string invariant)
    {
        WriteScalarHeader(writer, type, false, Utf8Length(invariant));
        writer.WriteString("value", invariant);
    }

    private static void WriteScalarHeader(Utf8JsonWriter writer, string type, bool isNull, long length)
    {
        writer.WriteString("type", type);
        writer.WriteBoolean("isNull", isNull);
        writer.WriteNumber("length", length);
    }

    private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);
}

/// <summary>
/// Carved-out streaming stage for single large text/binary cells
/// (varchar(max), nvarchar(max), varbinary(max)).
///
/// LIMITATION: production collection still calls <c>ISqlReader.GetValue</c>,
/// which fully materializes each cell before hashing, so this stage is not yet
/// wired into the read path and constant-memory reads are not claimed. When a
/// future reader yields bounded chunks, hashing those chunks here produces a
/// byte-identical digest to the one-shot codec below, so canonical hashes and
/// result equivalence stay complete while presentation caps
/// (--max-rows, agent cell budgets) only ever limit display.
/// </summary>
internal static class ChunkedCanonicalCellHash
{
    /// <summary>
    /// Hashes a text cell fed as sequential chunks. The digest equals SHA-256
    /// over the exact canonical scalar bytes the one-shot codec emits for the
    /// concatenated text, for every chunking (including mid-surrogate splits).
    /// </summary>
    public static byte[] HashString(IReadOnlyList<string> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        // Pass 1: total UTF-8 length for the scalar header, using the same
        // pair-aware scan as pass 2. (An incremental Encoder.GetByteCount
        // across chunks is not used: it drops a chunk-trailing high surrogate
        // instead of carrying it, undercounting a split pair 3 vs 4.)
        long totalUtf8Length = 0;
        char? countCarry = null;
        for (var scan = 0; scan < chunks.Count; scan++)
        {
            ArgumentNullException.ThrowIfNull(chunks[scan]);
            var piece = countCarry is char held ? held + chunks[scan] : chunks[scan];
            countCarry = null;
            if (scan < chunks.Count - 1 && piece.Length > 0 && char.IsHighSurrogate(piece[^1]))
            {
                countCarry = piece[^1];
                piece = piece[..^1];
            }
            totalUtf8Length += Encoding.UTF8.GetByteCount(piece);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAscii(hash, "{\"type\":\"string\",\"isNull\":false,\"length\":");
        AppendAscii(hash, totalUtf8Length.ToString(CultureInfo.InvariantCulture));
        AppendAscii(hash, ",\"value\":\"");

        // Pass 2: escaped value bytes. A high surrogate trailing a chunk is held
        // for the next chunk, so every piece handed to the escaper contains whole
        // pairs or true lone surrogates — identical context to the one-shot write.
        char? carry = null;
        for (var index = 0; index < chunks.Count; index++)
        {
            var piece = carry is char held ? held + chunks[index] : chunks[index];
            carry = null;
            if (index < chunks.Count - 1 && piece.Length > 0 && char.IsHighSurrogate(piece[^1]))
            {
                carry = piece[^1];
                piece = piece[..^1];
            }
            if (piece.Length > 0)
                AppendEscaped(hash, piece);
        }

        AppendAscii(hash, "\"}");
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Hashes a binary cell fed as sequential chunks. The digest equals SHA-256
    /// over the exact canonical scalar bytes the one-shot codec emits for the
    /// concatenated bytes, for every chunking.
    /// </summary>
    public static byte[] HashBytes(IReadOnlyList<byte[]> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        long totalLength = 0;
        foreach (var chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            totalLength += chunk.Length;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAscii(hash, "{\"type\":\"bytes\",\"isNull\":false,\"length\":");
        AppendAscii(hash, totalLength.ToString(CultureInfo.InvariantCulture));
        AppendAscii(hash, ",\"value\":\"");

        // Base64 encodes 3-byte groups; hold at most 2 bytes across chunk edges.
        // The base64 text still passes through the JSON escaper (the default
        // encoder escapes '+' as \u002B), split arbitrarily: escaping is per
        // character, so piece boundaries never change the bytes.
        var carry = new byte[3];
        var carryCount = 0;
        foreach (var chunk in chunks)
        {
            var offset = 0;
            if (carryCount > 0)
            {
                var take = Math.Min(3 - carryCount, chunk.Length);
                Buffer.BlockCopy(chunk, 0, carry, carryCount, take);
                carryCount += take;
                offset = take;
                if (carryCount == 3)
                {
                    AppendEscaped(hash, Convert.ToBase64String(carry));
                    carryCount = 0;
                }
            }
            var fullGroups = (chunk.Length - offset) / 3;
            if (fullGroups > 0)
            {
                AppendEscaped(hash, Convert.ToBase64String(chunk, offset, fullGroups * 3));
                offset += fullGroups * 3;
            }
            var rest = chunk.Length - offset;
            if (rest > 0)
            {
                Buffer.BlockCopy(chunk, offset, carry, 0, rest);
                carryCount = rest;
            }
        }
        if (carryCount > 0)
            AppendEscaped(hash, Convert.ToBase64String(carry, 0, carryCount));

        AppendAscii(hash, "\"}");
        return hash.GetHashAndReset();
    }

    private static void AppendAscii(IncrementalHash hash, string text) =>
        hash.AppendData(Encoding.ASCII.GetBytes(text));

    private static void AppendEscaped(IncrementalHash hash, string piece)
    {
        // Escape through the real JSON writer per piece and strip the surrounding
        // quotes, so interior escaping matches the one-shot write by construction.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            writer.WriteStringValue(piece);
        var bytes = stream.ToArray();
        hash.AppendData(bytes.AsSpan(1, bytes.Length - 2));
    }
}

internal sealed class CanonicalResultAccumulator : IDisposable
{
    private readonly HashingWriteStream _stream = new();
    private readonly Utf8JsonWriter _writer;
    private bool _inResultSet;
    private bool _completed;
    private long _currentRowCount;

    public CanonicalResultAccumulator()
    {
        _writer = new Utf8JsonWriter(_stream, new JsonWriterOptions { Indented = false });
        _writer.WriteStartObject();
        _writer.WritePropertyName("events");
        _writer.WriteStartArray();
        _writer.Flush();
    }

    public void BeginResultSet(IReadOnlyList<CanonicalColumn> columns)
    {
        EnsureActive();
        if (_inResultSet)
            throw new InvalidOperationException("The current result set must end before another begins.");

        ArgumentNullException.ThrowIfNull(columns);
        _writer.WriteStartObject();
        _writer.WriteString("kind", "resultSetStart");
        _writer.WritePropertyName("columns");
        _writer.WriteStartArray();
        foreach (var column in columns)
        {
            _writer.WriteStartObject();
            _writer.WriteNumber("ordinal", column.Ordinal);
            _writer.WriteString("name", column.Name);
            _writer.WriteString("dataType", column.DataType);
            _writer.WriteBoolean("allowNull", column.AllowNull);
            _writer.WriteEndObject();
        }
        _writer.WriteEndArray();
        _writer.WriteEndObject();
        _writer.Flush();

        _currentRowCount = 0;
        _inResultSet = true;
    }

    public void AddRow(IReadOnlyList<object?> values)
    {
        EnsureActive();
        if (!_inResultSet)
            throw new InvalidOperationException("A result set must begin before rows are added.");

        ArgumentNullException.ThrowIfNull(values);
        var preparedValues = values.Select(CanonicalScalarCodec.Prepare).ToArray();
        _writer.WriteStartObject();
        _writer.WriteString("kind", "row");
        _writer.WritePropertyName("values");
        _writer.WriteStartArray();
        foreach (var value in preparedValues)
            CanonicalScalarCodec.Write(_writer, value);
        _writer.WriteEndArray();
        _writer.WriteEndObject();
        _writer.Flush();
        _currentRowCount++;
    }

    public void EndResultSet()
    {
        EnsureActive();
        if (!_inResultSet)
            throw new InvalidOperationException("No result set is active.");

        _writer.WriteStartObject();
        _writer.WriteString("kind", "resultSetEnd");
        _writer.WriteNumber("rowCount", _currentRowCount);
        _writer.WriteEndObject();
        _writer.Flush();
        _inResultSet = false;
    }

    public void AddMessage(string messageKind, string value)
    {
        EnsureActive();
        if (_inResultSet)
            throw new InvalidOperationException("Messages can only be added between result sets.");

        ArgumentException.ThrowIfNullOrWhiteSpace(messageKind);
        ArgumentNullException.ThrowIfNull(value);
        var preparedValue = CanonicalScalarCodec.Prepare(value);
        _writer.WriteStartObject();
        _writer.WriteString("kind", "message");
        _writer.WriteString("messageKind", messageKind);
        _writer.WritePropertyName("value");
        CanonicalScalarCodec.Write(_writer, preparedValue);
        _writer.WriteEndObject();
        _writer.Flush();
    }

    public CanonicalResult Complete()
    {
        EnsureActive();
        if (_inResultSet)
            throw new InvalidOperationException("The current result set must end before completion.");

        _writer.WriteEndArray();
        _writer.WriteEndObject();
        _writer.Flush();
        _completed = true;

        var hash = Convert.ToHexString(_stream.GetHashAndReset());
        return new CanonicalResult(hash, _stream.GetFootprint());
    }

    public OutputFootprint SnapshotFootprint()
    {
        _writer.Flush();
        return _stream.GetFootprint();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }

    private void EnsureActive()
    {
        if (_completed)
            throw new InvalidOperationException("The canonical result is already complete.");
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytes;
        private long _newLines;
        private byte _lastByte;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
                return;

            _hash.AppendData(buffer);
            _bytes += buffer.Length;
            foreach (var value in buffer)
            {
                if (value == (byte)'\n')
                    _newLines++;
            }
            _lastByte = buffer[^1];
        }

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public OutputFootprint GetFootprint() =>
            new(_bytes, _bytes == 0 ? 0 : _newLines + (_lastByte == (byte)'\n' ? 0 : 1));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}