using System.Text;
using System.Text.RegularExpressions;
using SqlHarness.Core;

namespace SqlHarness.Cli.Infrastructure;

public sealed partial class OutputCaptureWriter(TextWriter inner) : TextWriter
{
    private readonly StringBuilder _captured = new();
    public override Encoding Encoding => inner.Encoding;
    public long Mark() => _captured.Length;
    public OutputFootprint GetAnsiFreeFootprint(long mark)
    {
        if (mark < 0 || mark > _captured.Length) throw new ArgumentOutOfRangeException(nameof(mark));
        var value = Ansi().Replace(_captured.ToString((int)mark, _captured.Length - (int)mark), string.Empty);
        var lines = value.Length == 0 ? 0 : value.Count(c => c == '\n') + (value[^1] == '\n' ? 0 : 1);
        return new(Encoding.UTF8.GetByteCount(value), lines);
    }
    public override void Write(char value) { _captured.Append(value); inner.Write(value); }
    public override void Write(string? value) { _captured.Append(value); inner.Write(value); }
    public override Task WriteAsync(string? value) { _captured.Append(value); return inner.WriteAsync(value); }
    public override void Flush() => inner.Flush();
    [GeneratedRegex("(?:\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\x1B\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex Ansi();
}
