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
    public const int MaxTools = 10;

    /// <summary>Budget for the whole tools/list catalog (schemas and descriptions) in UTF-8 bytes.</summary>
    public const long ToolsListBudgetBytes = 32L * 1024;

    /// <summary>
    /// Smallest accepted CallToolResult budget in UTF-8 bytes (4096). The
    /// minimum valid error envelope always fits inside this budget and JSON
    /// is never truncated to meet it.
    /// </summary>
    public const int MinCallToolResultBudgetBytes = 4096;

    /// <summary>
    /// Largest result-text budget a process operator or call may request
    /// (1048576). Per-call requests above the process ceiling are clamped.
    /// </summary>
    public const int MaxCallToolResultBudgetBytes = 1048576;

    /// <summary>Default per-cell character limit for result content.</summary>
    public const int DefaultMaximumCellCharacters = 512;

    /// <summary>Largest per-cell character limit an operator may configure (4096).</summary>
    public const int MaxCellCharactersLimit = 4096;

    /// <summary>Default per-call budget: UTF-8 bytes of the text block the model reads.</summary>
    public const int DefaultCallToolResultBudgetBytes = 64 * 1024;

    /// <summary>Default process ceiling for any single call (<c>--max-result-bytes</c>).</summary>
    public const int DefaultProcessResultCeilingBytes = 256 * 1024;

    /// <summary>Default process-wide DB time budget in seconds (15 minutes, spec section 7).</summary>
    public const int DefaultMaxOperationSeconds = 900;

    /// <summary>Smallest process-wide DB time budget in seconds.</summary>
    public const int MinOperationSeconds = 1;

    /// <summary>Largest process-wide DB time budget in seconds (24 hours).</summary>
    public const int MaxOperationSeconds = 86400;

    /// <summary>
    /// Resolves the effective per-call time budget from the process-operator
    /// maximum and an optional per-call request. A call may only lower the
    /// process maximum; a request above it is clamped down to it. Both inputs
    /// must sit inside 1..86400 seconds.
    /// </summary>
    public static int ResolveOperationSeconds(int processMaximumSeconds, int? callMaximumSeconds)
    {
        if (processMaximumSeconds < MinOperationSeconds || processMaximumSeconds > MaxOperationSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(processMaximumSeconds),
                processMaximumSeconds,
                $"Operation budget must be {MinOperationSeconds}..{MaxOperationSeconds} seconds.");
        var effective = callMaximumSeconds.HasValue
            ? Math.Min(callMaximumSeconds.Value, processMaximumSeconds)
            : processMaximumSeconds;
        if (effective < MinOperationSeconds || effective > MaxOperationSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(callMaximumSeconds),
                callMaximumSeconds,
                $"Operation budget must be {MinOperationSeconds}..{MaxOperationSeconds} seconds.");
        return effective;
    }
}
