using System.Text;
using System.Text.RegularExpressions;

using SqlHarness.Core;

namespace SqlHarness.Cli.Infrastructure;

public sealed partial class OutputCaptureWriter(TextWriter inner) : TextWriter
{
    private long _utf8Bytes;
    private long _characters;
    private long _newlines;
    private long _lineStart;
    private long _nextMark;
    private readonly Dictionary<long, (long Bytes, long Characters, long Newlines)> _marks = [];
    public override Encoding Encoding => inner.Encoding;
    public long Mark()
    {
        var mark = ++_nextMark;
        _marks.Add(mark, (_utf8Bytes, _characters, _newlines));
        return mark;
    }
    public OutputFootprint GetAnsiFreeFootprint(long mark)
    {
        if (!_marks.Remove(mark, out var start)) throw new ArgumentOutOfRangeException(nameof(mark));
        var hasTail = _characters > Math.Max(start.Characters, _lineStart);
        return new(_utf8Bytes - start.Bytes, _newlines - start.Newlines + (hasTail ? 1 : 0));
    }
    public override void Write(char value) { Track(value.ToString()); inner.Write(value); }
    public override void Write(string? value) { Track(value); inner.Write(value); }
    public override Task WriteAsync(string? value) { Track(value); return inner.WriteAsync(value); }
    public override void Flush() => inner.Flush();

    private void Track(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var visible = Ansi().Replace(value, string.Empty);
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
    [GeneratedRegex("(?:\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\x1B\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex Ansi();
}
