using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlHarness.Mcp;

/// <summary>Rewrites only unsupported historical initialize revisions before SDK parsing.</summary>
internal sealed class McpProtocolVersionRewriteInput(Stream inner) : Stream
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    private static readonly byte[] Fallback = Encoding.UTF8.GetBytes(McpHost.FallbackProtocolVersion);
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly byte[] _readBuffer = new byte[8192];
    private readonly Queue<byte> _output = new();
    private readonly List<byte> _frame = [];
    private bool _passthroughOversized;

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
            if (_passthroughOversized)
            {
                _output.Enqueue(value);
                if (value == (byte)'\n') _passthroughOversized = false;
                continue;
            }

            if (value == (byte)'\n')
            {
                EmitFrame(includeNewline: true);
                continue;
            }

            if (_frame.Count >= MaxFrameBytes)
            {
                foreach (var buffered in _frame) _output.Enqueue(buffered);
                _frame.Clear();
                _output.Enqueue(value);
                _passthroughOversized = true;
            }
            else
            {
                _frame.Add(value);
            }
        }
    }

    private void FlushPartialFrame()
    {
        if (_frame.Count != 0) EmitFrame(includeNewline: false);
    }

    private void EmitFrame(bool includeNewline)
    {
        var frame = _frame.ToArray();
        var rewritten = TryRewrite(frame);
        foreach (var value in rewritten ?? frame) _output.Enqueue(value);
        if (includeNewline) _output.Enqueue((byte)'\n');
        _frame.Clear();
    }

    private static byte[]? TryRewrite(byte[] frame)
    {
        try
        {
            var message = JsonNode.Parse(frame);
            if (message is not JsonObject jsonObject
                || jsonObject["method"] is not JsonValue methodValue
                || jsonObject["params"] is not JsonObject parameters
                || !methodValue.TryGetValue<string>(out var method)
                || !string.Equals(method, "initialize", StringComparison.Ordinal)
                || parameters["protocolVersion"] is not JsonValue versionValue
                || !versionValue.TryGetValue<string>(out var version)
                || (version != "2024-11-05" && version != "2025-03-26"))
            {
                return null;
            }

            parameters["protocolVersion"] = Encoding.UTF8.GetString(Fallback);
            return Encoding.UTF8.GetBytes(message.ToJsonString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private int Drain(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, _output.Count);
        for (var i = 0; i < count; i++) destination[i] = _output.Dequeue();
        return count;
    }
}
