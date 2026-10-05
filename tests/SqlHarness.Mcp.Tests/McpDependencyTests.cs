using System.Reflection;

using SqlHarness.Core;

namespace SqlHarness.Mcp.Tests;

public sealed class McpDependencyTests
{
    private static IReadOnlySet<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Core_does_not_reference_mcp_sdk_or_console_shell()
    {
        var names = ReferencedAssemblyNames(typeof(ISqlHarnessModule).Assembly);

        Assert.DoesNotContain("SqlHarness.Mcp", names);
        Assert.DoesNotContain("SqlHarness.Cli", names);
        Assert.DoesNotContain("ModelContextProtocol", names);
        Assert.DoesNotContain("ModelContextProtocol.Core", names);
        Assert.DoesNotContain("Spectre.Console.Cli", names);
    }

    [Fact]
    public void Mcp_wiring_flows_to_output_without_console_shell()
    {
        // T1 ships csproj + wiring only (McpHost.cs lands in T2), so the empty
        // SqlHarness.Mcp manifest carries no Core/SDK edges yet; the edges are
        // proven at build level (ProjectReference + Mcp.deps.json) and must stay
        // free of console-shell and test assemblies once T2 adds code.
        var mcp = Assembly.Load("SqlHarness.Mcp");
        var directory = Path.GetDirectoryName(mcp.Location);
        Assert.Equal(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            (directory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar));

        Assert.True(
            File.Exists(Path.Combine(directory!, "SqlHarness.Core.dll")),
            "The MCP->Core project reference must deploy SqlHarness.Core next to SqlHarness.Mcp.");

        var names = ReferencedAssemblyNames(mcp);
        Assert.DoesNotContain("Spectre.Console.Cli", names);
        Assert.DoesNotContain("SqlHarness.Cli", names);
        Assert.DoesNotContain("xunit", names);
    }
}