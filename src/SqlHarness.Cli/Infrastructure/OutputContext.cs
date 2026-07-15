using SqlHarness.Core;

namespace SqlHarness.Cli.Infrastructure;

public sealed class OutputContext(TextWriter writer)
{
    public OutputCaptureWriter Capture { get; } = new(writer);
    public long Begin() => Capture.Mark();
    public async Task<int> CompleteAsync(SqlHarnessOutcome outcome, long mark, CancellationToken ct)
    {
        if (outcome.EmissionReceipt is null) return (int)outcome.ExitCode;
        var completed = await outcome.EmissionReceipt.CompleteAsync(Capture.GetAnsiFreeFootprint(mark), ct);
        return completed == SqlHarnessExitCode.LocalStorage ? (int)completed : (int)outcome.ExitCode;
    }
}
