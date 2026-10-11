using ModelContextProtocol.Protocol;

namespace SqlHarness.Mcp;

/// <summary>
/// Client identity for one serve process. The first non-null client info wins,
/// matching the journal's first-non-null session rule.
/// </summary>
public sealed class McpClientIdentity
{
    private Implementation? _client;
    private string? _title;
    private string? _protocolVersion;

    public void Record(Implementation? clientInfo)
    {
        if (clientInfo is not null)
        {
            Interlocked.CompareExchange(ref _client, clientInfo, null);
            // A title may arrive after the first client info lacked one; it fills NULL only.
            if (!string.IsNullOrWhiteSpace(clientInfo.Title))
                Interlocked.CompareExchange(ref _title, clientInfo.Title, null);
        }
    }

    /// <summary>Negotiated revision of a request; the first non-blank one wins.</summary>
    public void RecordProtocolVersion(string? protocolVersion)
    {
        if (!string.IsNullOrWhiteSpace(protocolVersion))
            Interlocked.CompareExchange(ref _protocolVersion, protocolVersion, null);
    }

    public string? Name => Volatile.Read(ref _client)?.Name;

    public string? Version => Volatile.Read(ref _client)?.Version;

    public string? Title => Volatile.Read(ref _title);

    public string? ProtocolVersion => Volatile.Read(ref _protocolVersion);
}