using System.Diagnostics;
using System.Text.RegularExpressions;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

/// <summary>
/// Plan 060: the CLI stays consistent with MCP. ping, schema, counts, space, qstop, indexes and
/// validate are gone; historical journal rows with those operation names stay readable (see
/// JournalReaderTests).
/// </summary>
public sealed class RemovedCommandsTests
{
    private static readonly string[] Remaining =
    [
        "artifact", "capabilities", "compare", "dashboard", "doctor", "mcp", "measure", "plan", "query", "snapshot", "watch",
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

    [Theory]
    [InlineData("ping")]
    [InlineData("schema")]
    [InlineData("counts")]
    [InlineData("space")]
    [InlineData("qstop")]
    [InlineData("indexes")]
    [InlineData("validate")]
    public async Task Removed_commands_are_unknown_commands(string command)
    {
        var module = new RecordingModule();

        var text = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([command, "dev"]);
        var json = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync([command, "dev", "--json"]);

        Assert.Equal(-1, text);
        Assert.Equal((int)SqlHarnessExitCode.Safety, json);
        Assert.Empty(module.Operations);
    }

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }
}