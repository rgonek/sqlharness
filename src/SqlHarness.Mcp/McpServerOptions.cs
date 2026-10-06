namespace SqlHarness.Mcp;

/// <summary>
/// Startup configuration for one MCP server process. A process serves a
/// single closed profile with fixed vars (spec section 3); tools cannot
/// change the target or credentials afterwards.
/// </summary>
public sealed class McpServerOptions
{
    /// <summary>Resolve a target from each target-dependent tool request.</summary>
    public bool RequestScope { get; init; }

    /// <summary>Profiles permitted for request-scoped mode.</summary>
    public IReadOnlyList<string> AllowedProfiles { get; init; } = [];

    /// <summary>Closed target profile selected by the operator at startup.</summary>
    public string Profile { get; init; } = string.Empty;

    /// <summary>Fixed profile variables (key=value pairs from --var).</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute input roots that allow file inputs. Empty by default, which
    /// means no file inputs are admitted (spec section 5).
    /// </summary>
    public IReadOnlyList<string> InputRoots { get; init; } = [];

    /// <summary>
    /// Process-wide cap for one serialized tool response in UTF-8 bytes
    /// (4096..1048576, default 16384). A single tool call may only lower it.
    /// </summary>
    public int MaxResultBytes { get; init; } = (int)McpLimits.CallToolResultBudgetBytes;

    /// <summary>
    /// Process-wide time budget for one database call in seconds (1..86400,
    /// default 900). A single tool call may only lower it.
    /// </summary>
    public int MaxOperationSeconds { get; init; } = McpLimits.DefaultMaxOperationSeconds;
}
