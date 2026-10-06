using System.Text;
using System.Text.Json;

namespace SqlHarness.Mcp;

/// <summary>
/// Rejects ambiguous JSON-RPC frames before the MCP SDK materializes argument
/// dictionaries (which otherwise discard duplicate property names). A bad
/// frame is replaced with a content-free unknown-tool call using only its
/// unambiguous correlation id, allowing the stdio session to continue.
/// </summary>
internal sealed class McpDuplicateJsonFieldGuardInput(Stream inner) : Stream
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    private static readonly byte[] RejectedFrame = Encoding.UTF8.GetBytes(
        "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"tools/call\",\"params\":{\"name\":\"__invalid_request__\",\"arguments\":{}}}\n");
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly byte[] _readBuffer = new byte[8192];
    private readonly Queue<byte> _output = new();
    private readonly List<byte> _frame = [];
    private bool _oversized;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        while (_output.Count == 0)
        {
            var read = _inner.Read(_readBuffer, 0, _readBuffer.Length);
            if (read == 0)
            {
                FlushPartialFrame();
                if (_output.Count == 0) return 0;
                break;
            }
            Process(_readBuffer.AsSpan(0, read));
        }
        return Drain(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        while (_output.Count == 0)
        {
            var read = await _inner.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                FlushPartialFrame();
                if (_output.Count == 0) return 0;
                break;
            }
            Process(_readBuffer.AsSpan(0, read));
        }
        return Drain(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private void Process(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                EmitFrame(includeNewline: true);
                continue;
            }
            if (!_oversized)
            {
                if (_frame.Count >= MaxFrameBytes) _oversized = true;
                else _frame.Add(value);
            }
        }
    }

    private void FlushPartialFrame()
    {
        if (_frame.Count != 0 || _oversized) EmitFrame(includeNewline: false);
    }

    private void EmitFrame(bool includeNewline)
    {
        var reject = _oversized || HasDuplicateProperties(_frame);
        if (reject)
        {
            var id = _oversized ? null : GetUnambiguousId(_frame);
            var replacement = id is null
                ? RejectedFrame
                : Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"__invalid_request__\",\"arguments\":{}}}" + (includeNewline ? "\n" : ""));
            foreach (var value in replacement) _output.Enqueue(value);
        }
        else
        {
            foreach (var value in _frame) _output.Enqueue(value);
            if (includeNewline) _output.Enqueue((byte)'\n');
        }
        _frame.Clear();
        _oversized = false;
    }

    private static bool HasDuplicateProperties(List<byte> frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame.ToArray());
            return HasDuplicateProperties(document.RootElement);
        }
        catch (JsonException)
        {
            return false; // The SDK retains its established malformed-frame handling.
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (HasDuplicateProperties(child)) return true;
        }
        return false;
    }

    private static string? GetUnambiguousId(List<byte> frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            JsonElement id = default;
            var found = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "id", StringComparison.Ordinal)) continue;
                if (found) return null;
                found = true;
                id = property.Value;
            }
            if (!found || id.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined) return null;
            return id.GetRawText();
        }
        catch (JsonException) { return null; }
    }

    private int Drain(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, _output.Count);
        for (var i = 0; i < count; i++) destination[i] = _output.Dequeue();
        return count;
    }
}
