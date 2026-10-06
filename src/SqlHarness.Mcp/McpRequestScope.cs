namespace SqlHarness.Mcp;

/// <summary>Target scope supplied to one request-scoped MCP tool call.</summary>
public sealed record McpRequestScope(
    string Profile,
    IReadOnlyDictionary<string, string> Vars);