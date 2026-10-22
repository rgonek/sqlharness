namespace SqlHarness.Core;

/// <summary>Accounts raw bytes admitted from external parameter files for one logical call.</summary>
public sealed class ParameterFileIngressBudget
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumTotalBytes = 32 * 1024 * 1024;

    private int _usedBytes;

    /// <summary>Raw bytes admitted so far, counting each file occurrence once.</summary>
    public int UsedBytes => _usedBytes;

    /// <summary>
    /// Reads one caller-supplied stream with a hard bound and sentinel byte. The stream
    /// is read incrementally, so a growing file cannot bypass either limit.
    /// </summary>
    public async Task<byte[]> ReadAsync(Stream stream, string? path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        var remaining = MaximumTotalBytes - _usedBytes;
        var allowed = Math.Min(MaximumFileBytes, remaining);
        using var output = new MemoryStream(Math.Min(allowed, 8192));
        var buffer = new byte[8192];
        while (true)
        {
            var request = Math.Min(buffer.Length, allowed - (int)output.Length + 1);
            var read = await stream.ReadAsync(buffer.AsMemory(0, request), cancellationToken);
            if (read == 0)
                break;
            if (output.Length + read > allowed)
            {
                var message = remaining < MaximumFileBytes
                    ? $"Parameter file '{path}' exceeds the 32 MiB total raw parameter-file limit."
                    : $"Parameter file '{path}' exceeds the 16 MiB raw parameter-file limit.";
                throw new ParameterSetFileException(message);
            }
            output.Write(buffer, 0, read);
        }

        _usedBytes += checked((int)output.Length);
        return output.ToArray();
    }
}
