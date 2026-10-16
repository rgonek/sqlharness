using System.Diagnostics;
using System.Text.RegularExpressions;


namespace SqlHarness.Tests.Cli;

/// <summary>
/// Plan 060: the CLI command surface stays consistent with the eight MCP tools.
/// </summary>
public sealed class CliCommandSurfaceTests
{
    private static readonly string[] Remaining =
    [
        "artifact", "capabilities", "compare", "dashboard", "doctor", "mcp", "measure", "mutation", "plan", "query", "snapshot", "watch",
    ];

    [Fact]
    public async Task Root_help_lists_exactly_the_remaining_commands()
    {
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "sqlharness.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{cliAssembly}\" --help")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        Assert.NotNull(process);
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, standardError);
        var commands = new List<string>();
        var inCommands = false;
        foreach (var line in standardOutput.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("COMMANDS", StringComparison.Ordinal)) { inCommands = true; continue; }
            if (!inCommands) continue;
            var match = Regex.Match(line, @"^\s{2,4}(\S+)");
            if (match.Success) commands.Add(match.Groups[1].Value);
        }

        Assert.Equal(Remaining.Order(StringComparer.Ordinal), commands.Order(StringComparer.Ordinal));
    }
}
