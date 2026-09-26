using SqlHarness.Core;
using SqlHarness.Cli.Commands;

namespace SqlHarness.Cli.Infrastructure;

public sealed class OutputContext(TextWriter writer)
{
    public OutputCaptureWriter Capture { get; } = new(writer);
    public OutputMode Mode { get; private set; } = OutputMode.Text;
    public string Command { get; private set; } = "unknown";
    public void Configure(IEnumerable<string> args)
    {
        Mode = OutputMode.Text;
        var values = args.ToArray();
        Command = values.FirstOrDefault(value => !value.StartsWith("-", StringComparison.Ordinal))?.ToLowerInvariant() ?? "unknown";
        if (values.Any(value => value.Equals("--output", StringComparison.OrdinalIgnoreCase) || value.StartsWith("--output=", StringComparison.OrdinalIgnoreCase)))
            Mode = OutputMode.Agent;
        else if (values.Contains("--json-summary", StringComparer.OrdinalIgnoreCase)) Mode = OutputMode.JsonSummary;
        else if (values.Contains("--json", StringComparer.OrdinalIgnoreCase)) Mode = OutputMode.Json;
    }
    public long Begin() => Capture.Mark();
    public async Task<int> CompleteAsync(SqlHarnessOutcome outcome, long mark, CancellationToken ct)
    {
        if (outcome.EmissionReceipt is null) return (int)outcome.ExitCode;
        var completed = await outcome.EmissionReceipt.CompleteAsync(Capture.GetAnsiFreeFootprint(mark), ct);
        return completed == SqlHarnessExitCode.LocalStorage ? (int)completed : (int)outcome.ExitCode;
    }
}
