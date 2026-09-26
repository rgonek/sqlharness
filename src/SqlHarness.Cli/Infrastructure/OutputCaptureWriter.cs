using System.Text;
using System.Text.RegularExpressions;

using SqlHarness.Core;

namespace SqlHarness.Cli.Infrastructure;

public sealed partial class OutputCaptureWriter : TextWriter
{
    private readonly TextWriter _inner;
    private long _utf8Bytes;
    private long _characters;
    private long _newlines;
    private long _lineStart;
    private long _nextMark;
    private char? _pendingHighSurrogate;
    private readonly Dictionary<long, (long Bytes, long Characters, long Newlines)> _marks = [];

    public OutputCaptureWriter(TextWriter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        NewLine = inner.NewLine;
    }

    public override Encoding Encoding => _inner.Encoding;
    public long Mark()
    {
        FlushPendingSurrogate();
        var mark = ++_nextMark;
        _marks.Add(mark, (_utf8Bytes, _characters, _newlines));
        return mark;
    }
    public OutputFootprint GetAnsiFreeFootprint(long mark)
    {
        FlushPendingSurrogate();
        if (!_marks.Remove(mark, out var start)) throw new ArgumentOutOfRangeException(nameof(mark));
        var hasTail = _characters > Math.Max(start.Characters, _lineStart);
        return new(_utf8Bytes - start.Bytes, _newlines - start.Newlines + (hasTail ? 1 : 0));
    }
    public override void Write(char value) { Track(value.ToString()); _inner.Write(value); }
    public override void Write(string? value) { Track(value); _inner.Write(value); }
    public override Task WriteAsync(string? value) { Track(value); return _inner.WriteAsync(value); }
    public override void Flush() => _inner.Flush();

    private void Track(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var visible = Ansi().Replace(value, string.Empty);
        if (_pendingHighSurrogate is char pending)
        {
            visible = pending + visible;
            _pendingHighSurrogate = null;
        }
        if (visible.Length > 0 && char.IsHighSurrogate(visible[^1]))
        {
            _pendingHighSurrogate = visible[^1];
            visible = visible[..^1];
        }
        _utf8Bytes += Encoding.UTF8.GetByteCount(visible);
        foreach (var character in visible)
        {
            _characters++;
            if (character == '\n')
            {
                _newlines++;
                _lineStart = _characters;
            }
        }
    }

    private void FlushPendingSurrogate()
    {
        if (_pendingHighSurrogate is not char pending) return;
        _utf8Bytes += Encoding.UTF8.GetByteCount([pending]);
        _characters++;
        _pendingHighSurrogate = null;
    }
    [GeneratedRegex("(?:\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\x1B\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex Ansi();
}
