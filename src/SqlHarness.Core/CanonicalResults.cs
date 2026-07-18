using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlHarness.Core;

public sealed record OutputFootprint(long Bytes, long Lines)
{
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
        var preparedValues = values.Select(PrepareScalar).ToArray();
        _writer.WriteStartObject();
        _writer.WriteString("kind", "row");
        _writer.WritePropertyName("values");
        _writer.WriteStartArray();
        foreach (var value in preparedValues)
            WriteScalar(value);
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
        var preparedValue = PrepareScalar(value);
        _writer.WriteStartObject();
        _writer.WriteString("kind", "message");
        _writer.WriteString("messageKind", messageKind);
        _writer.WritePropertyName("value");
        WriteScalar(preparedValue);
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

    private static PreparedScalar PrepareScalar(object? value) => value switch
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

    private void WriteScalar(PreparedScalar scalar)
    {
        var value = scalar.Value;
        _writer.WriteStartObject();
        if (value is null or DBNull)
        {
            WriteScalarHeader("null", true, 0);
            _writer.WriteNull("value");
            _writer.WriteEndObject();
            return;
        }

        switch (value)
        {
            case string text:
                WriteScalarHeader("string", false, Utf8Length(text));
                _writer.WriteString("value", text);
                break;
            case char character:
                var characterText = character.ToString();
                WriteScalarHeader("char", false, Utf8Length(characterText));
                _writer.WriteString("value", characterText);
                break;
            case bool boolean:
                WriteScalarHeader("boolean", false, boolean ? 4 : 5);
                _writer.WriteBoolean("value", boolean);
                break;
            case byte number:
                WriteInteger("byte", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case sbyte number:
                WriteInteger("sbyte", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case short number:
                WriteInteger("int16", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case ushort number:
                WriteInteger("uint16", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case int number:
                WriteInteger("int32", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case uint number:
                WriteInteger("uint32", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case long number:
                WriteInteger("int64", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case ulong number:
                WriteInteger("uint64", number, number.ToString(CultureInfo.InvariantCulture));
                break;
            case decimal number:
                var decimalText = number.ToString(CultureInfo.InvariantCulture);
                WriteScalarHeader("decimal", false, Utf8Length(decimalText));
                _writer.WriteNumber("value", number);
                break;
            case float number when float.IsFinite(number):
                var singleText = number.ToString("R", CultureInfo.InvariantCulture);
                WriteScalarHeader("single", false, Utf8Length(singleText));
                _writer.WriteNumber("value", number);
                break;
            case double number when double.IsFinite(number):
                var doubleText = number.ToString("R", CultureInfo.InvariantCulture);
                WriteScalarHeader("double", false, Utf8Length(doubleText));
                _writer.WriteNumber("value", number);
                break;
            case byte[] bytes:
                var base64 = Convert.ToBase64String(bytes);
                WriteScalarHeader("bytes", false, bytes.LongLength);
                _writer.WriteString("value", base64);
                break;
            case Guid guid:
                WriteInvariantText("guid", guid.ToString("D"));
                break;
            case DateTime dateTime:
                WriteInvariantText("dateTime", dateTime.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dateTimeOffset:
                WriteInvariantText("dateTimeOffset", dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                WriteInvariantText("date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                WriteInvariantText("time", time.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                WriteInvariantText("timeSpan", timeSpan.ToString("c", CultureInfo.InvariantCulture));
                break;
            case IFormattable formattable:
                WriteInvariantText(
                    formattable.GetType().FullName ?? formattable.GetType().Name,
                    scalar.InvariantValue!);
                break;
            default:
                throw new NotSupportedException(
                    $"Canonical scalar type '{value.GetType().FullName}' is not supported.");
        }

        _writer.WriteEndObject();
    }

    private void WriteInteger(string type, long value, string invariant)
    {
        WriteScalarHeader(type, false, Utf8Length(invariant));
        _writer.WriteNumber("value", value);
    }

    private void WriteInteger(string type, ulong value, string invariant)
    {
        WriteScalarHeader(type, false, Utf8Length(invariant));
        _writer.WriteNumber("value", value);
    }

    private void WriteInvariantText(string type, string invariant)
    {
        WriteScalarHeader(type, false, Utf8Length(invariant));
        _writer.WriteString("value", invariant);
    }

    private void WriteScalarHeader(string type, bool isNull, long length)
    {
        _writer.WriteString("type", type);
        _writer.WriteBoolean("isNull", isNull);
        _writer.WriteNumber("length", length);
    }

    private void EnsureActive()
    {
        if (_completed)
            throw new InvalidOperationException("The canonical result is already complete.");
    }

    private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);

    private readonly record struct PreparedScalar(object? Value, string? InvariantValue);

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