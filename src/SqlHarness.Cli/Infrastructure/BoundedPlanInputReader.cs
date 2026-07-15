using System.Text;
using SqlHarness.Core;

namespace SqlHarness.Cli.Infrastructure;

public sealed record PlanInput(Stream Stream);

internal sealed record BoundedPlanInput(string Text, OutputFootprint Footprint);
internal sealed class PlanInputSafetyException(string message) : Exception(message);

internal static class BoundedPlanInputReader
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, true, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, true, true);

    public static async Task<BoundedPlanInput> ReadAsync(Stream input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (bytes.Length <= MaximumBytes)
        {
            var remaining = MaximumBytes + 1 - (int)bytes.Length;
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) break;
            bytes.Write(buffer, 0, read);
        }
        if (bytes.Length > MaximumBytes)
            throw new PlanInputSafetyException("The execution plan input exceeds the 16 MiB safety limit.");

        var raw = bytes.ToArray();
        var (encoding, offset) = DetectEncoding(raw);
        string text;
        try { text = encoding.GetString(raw, offset, raw.Length - offset); }
        catch (DecoderFallbackException) { throw new PlanInputSafetyException("The execution plan input has invalid text encoding."); }
        var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text[^1] == '\n' ? 0 : 1);
        return new(text, new OutputFootprint(raw.Length, lines));
    }

    private static (Encoding Encoding, int Offset) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) return (Utf8, Encoding.UTF8.Preamble.Length);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)) return (Utf16Le, Encoding.Unicode.Preamble.Length);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble)) return (Utf16Be, Encoding.BigEndianUnicode.Preamble.Length);
        return (Utf8, 0);
    }
}
