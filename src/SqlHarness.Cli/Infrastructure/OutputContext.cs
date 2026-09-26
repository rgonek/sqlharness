using SqlHarness.Core;
using System.Globalization;
using SqlHarness.Cli.Commands;

namespace SqlHarness.Cli.Infrastructure;

public sealed class OutputContext(TextWriter writer)
{
    public OutputCaptureWriter Capture { get; } = new(writer);
    public OutputMode Mode { get; private set; } = OutputMode.Text;
    public string Command { get; private set; } = "unknown";
    public AgentOutputOptions AgentOptions { get; private set; } = new();
    public void Configure(IEnumerable<string> args)
    {
        Mode = OutputMode.Text;
        var values = args.ToArray();
        AgentOptions = new(
            ReadIntegerOption(values, "--max-output-bytes", 16 * 1024),
            ReadIntegerOption(values, "--max-cell-chars", 512));
        Command = values.FirstOrDefault(value => !value.StartsWith("-", StringComparison.Ordinal))?.ToLowerInvariant() ?? "unknown";
        if (values.Any(value => value.Equals("--output", StringComparison.OrdinalIgnoreCase) || value.StartsWith("--output=", StringComparison.OrdinalIgnoreCase)))
            Mode = OutputMode.Agent;
        else if (values.Contains("--json-summary", StringComparer.OrdinalIgnoreCase)) Mode = OutputMode.JsonSummary;
        else if (values.Contains("--json", StringComparer.OrdinalIgnoreCase)) Mode = OutputMode.Json;
    }

    private static int ReadIntegerOption(IReadOnlyList<string> values, string name, int fallback)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (value.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(value[(name.Length + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inline) ? inline : fallback;
            if (value.Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < values.Count)
                return int.TryParse(values[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var next) ? next : fallback;
        }
        return fallback;
    }
    public long Begin() => Capture.Mark();
    public async Task<int> CompleteAsync(SqlHarnessOutcome outcome, long mark, CancellationToken ct)
    {
        if (outcome.EmissionReceipt is null) return (int)outcome.ExitCode;
        var completed = await outcome.EmissionReceipt.CompleteAsync(Capture.GetAnsiFreeFootprint(mark), ct);
        return completed == SqlHarnessExitCode.LocalStorage ? (int)completed : (int)outcome.ExitCode;
    }
}
