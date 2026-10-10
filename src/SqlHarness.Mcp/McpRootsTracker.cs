using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

#pragma warning disable MCP9005 // Roots is deprecated as of 2026-07-28; handshake revisions still use it.

namespace SqlHarness.Mcp;

/// <summary>
/// In-memory snapshot of the client's workspace roots. Fetches run in the background,
/// never block a tool call and never write the journal; the journal reads
/// <see cref="Current"/> at each operation start. A newer fetch cancels an older one,
/// and only the newest fetch may publish. Stderr carries counts and exception type
/// names only, never URIs or names.
/// </summary>
internal sealed class McpRootsTracker(TextWriter log, CancellationToken lifetime, TimeSpan? timeout = null)
{
    internal const int MaximumRoots = 32;
    internal const int MaximumUriLength = 2048;
    internal const int MaximumNameLength = 256;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private CancellationTokenSource? _newest;
    private string? _snapshot;

    public string? Current => Volatile.Read(ref _snapshot);

    public Task Refresh(Func<CancellationToken, ValueTask<ListRootsResult>> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationTokenSource fetch;
        lock (_gate)
        {
            _newest?.Cancel();
            fetch = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            fetch.CancelAfter(_timeout);
            _newest = fetch;
        }

        return Task.Run(() => FetchAsync(request, fetch));
    }

    private async Task FetchAsync(Func<CancellationToken, ValueTask<ListRootsResult>> request, CancellationTokenSource fetch)
    {
        try
        {
            var result = await request(fetch.Token);
            var (json, kept, total) = Normalize(result.Roots);
            lock (_gate)
            {
                if (!ReferenceEquals(_newest, fetch))
                    return;
                Volatile.Write(ref _snapshot, json);
            }

            if (kept < total)
                Write($"sqlharness-mcp: roots: kept {kept} of {total}.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || !IsNewest(fetch))
        {
            // Host shutdown or superseded by a newer fetch: nothing to report.
        }
        catch (OperationCanceledException)
        {
            Write("sqlharness-mcp: roots/list timed out; keeping the previous roots.");
        }
        catch (Exception exception)
        {
            Write($"sqlharness-mcp: roots/list failed ({exception.GetType().Name}); keeping the previous roots.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_newest, fetch))
                    _newest = null;
            }

            fetch.Dispose();
        }
    }

    private bool IsNewest(CancellationTokenSource fetch)
    {
        lock (_gate)
            return ReferenceEquals(_newest, fetch);
    }

    private void Write(string line)
    {
        lock (log)
            log.WriteLine(line);
    }

    internal static (string Json, int Kept, int Total) Normalize(IList<Root>? roots)
    {
        var total = roots?.Count ?? 0;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var root in (roots ?? []).Take(MaximumRoots))
            {
                writer.WriteStartObject();
                writer.WriteString("uri", Cap(root.Uri ?? string.Empty, MaximumUriLength));
                if (root.Name is { } name)
                    writer.WriteString("name", Cap(name, MaximumNameLength));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return (Encoding.UTF8.GetString(buffer.ToArray()), Math.Min(total, MaximumRoots), total);
    }

    private static string Cap(string value, int limit) => value.Length <= limit ? value : value[..limit];
}