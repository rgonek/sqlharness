namespace SqlHarness.Mcp;

/// <summary>
/// Single home for MCP v1 byte budgets. SQL/plan ingress (16 MiB) matches the
/// CLI/Core SQL bound, param-set files (64 KiB) match the strict .sqljson
/// contract, and the 1 MiB inline frame cap means larger payloads must arrive
/// via files under explicit operator input roots.
/// </summary>
public static class McpLimits
{
    /// <summary>Maximum admitted SQL text, inline or file, in UTF-8 bytes.</summary>
    public const long MaxSqlBytes = 16L * 1024 * 1024;

    /// <summary>Maximum admitted plan document, inline or file, in UTF-8 bytes.</summary>
    public const long MaxPlanBytes = 16L * 1024 * 1024;

    /// <summary>Maximum admitted .sqljson parameter-set file size in bytes.</summary>
    public const int MaxParameterSetBytes = 64 * 1024;

    /// <summary>
    /// Maximum inline argument payload in UTF-8 bytes. Anything larger must be
    /// placed in a file under an input root and passed by path.
    /// </summary>
    public const long MaxInlineBytes = 1L * 1024 * 1024;

    /// <summary>Maximum number of tools served by one MCP server process.</summary>
    public const int MaxTools = 11;

    /// <summary>Budget for the whole tools/list catalog (schemas and descriptions) in UTF-8 bytes.</summary>
    public const long ToolsListBudgetBytes = 32L * 1024;

    /// <summary>Budget for one serialized CallToolResult in UTF-8 bytes (enforced in T4).</summary>
    public const long CallToolResultBudgetBytes = 16L * 1024;
}
