namespace SqlHarness.Mcp;

/// <summary>
/// Startup configuration for one MCP server process. A process serves a
/// single closed profile with fixed vars (spec section 3); tools cannot
/// change the target or credentials afterwards.
/// </summary>
public sealed class McpServerOptions
{
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
}
