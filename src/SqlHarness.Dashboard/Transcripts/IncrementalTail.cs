namespace SqlHarness.Dashboard.Transcripts;

internal sealed record TailCursor(long Size, DateTimeOffset Mtime, long Offset);

internal sealed record TailRead(TailCursor Cursor, long BytesRead, bool Completed);

internal delegate void TranscriptLineHandler(ReadOnlySpan<byte> line);

/// <summary>
/// Reads complete '\n'-terminated lines appended since the cursor, up to a byte budget, without
/// blocking the writer. A trailing partial line stays unread; a shrunk or older file restarts at offset 0.
/// </summary>
internal static class IncrementalTail
{
    private const int ChunkBytes = 64 * 1024;

    internal static TailRead Read(string path, TailCursor? cursor, long budget, TranscriptLineHandler onLine)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ArgumentNullException.ThrowIfNull(onLine);

        var info = new FileInfo(path);
        var size = info.Length;
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var restarted = cursor is null || size < cursor.Size || mtime < cursor.Mtime;
        var offset = restarted ? 0 : cursor!.Offset;

        if (!restarted && size == cursor!.Size && mtime == cursor.Mtime && cursor.Offset >= size)
            return new TailRead(cursor, 0, true);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        using var pending = new MemoryStream();
        var buffer = new byte[ChunkBytes];
        long bytesRead = 0;
        var consumed = offset;

        while (bytesRead < budget && stream.Position < size)
        {
            var requested = (int)Math.Min(Math.Min(buffer.Length, budget - bytesRead), size - stream.Position);
            var count = stream.Read(buffer, 0, requested);
            if (count == 0)
                break;

            bytesRead += count;
            var span = buffer.AsSpan(0, count);
            while (span.IndexOf((byte)'\n') is var newline && newline >= 0)
            {
                var prefix = span[..newline];
                if (pending.Length > 0)
                {
                    pending.Write(prefix);
                    onLine(pending.GetBuffer().AsSpan(0, (int)pending.Length).TrimEnd((byte)'\r'));
                    pending.SetLength(0);
                }
                else
                {
                    onLine(prefix.TrimEnd((byte)'\r'));
                }

                consumed = stream.Position - span.Length + newline + 1;
                span = span[(newline + 1)..];
            }

            pending.Write(span);
        }

        var completed = consumed == size && pending.Length == 0;
        return new TailRead(new TailCursor(size, mtime, consumed), bytesRead, completed);
    }
}
