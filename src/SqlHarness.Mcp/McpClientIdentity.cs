using ModelContextProtocol.Protocol;

namespace SqlHarness.Mcp;

/// <summary>
/// Client identity for one serve process. The first non-null client info wins,
/// matching the journal's first-non-null session rule.
/// </summary>
public sealed class McpClientIdentity
{
    private Implementation? _client;

    public void Record(Implementation? clientInfo)
    {
        if (clientInfo is not null)
            Interlocked.CompareExchange(ref _client, clientInfo, null);
    }

    public string? Name => Volatile.Read(ref _client)?.Name;

    public string? Version => Volatile.Read(ref _client)?.Version;
}