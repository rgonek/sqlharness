namespace SqlHarness.Core;

public sealed record SqlHarnessGainSummary(
    long Executions, long Failures, long DurationMilliseconds,
    long RawBytes, long RawLines, long EmittedBytes, long EmittedLines,
    long RawEstimatedTokens, long EmittedEstimatedTokens, long SavedEstimatedTokens)
{
    public long NetEstimatedTokens => RawEstimatedTokens - EmittedEstimatedTokens;
    public string EstimationMethod => OutputFootprint.EstimationMethod;
    public double SavingsPercentage =>
        RawEstimatedTokens == 0 ? 0 : (double)NetEstimatedTokens / RawEstimatedTokens * 100;
}

public sealed record SqlHarnessGainReport(
    SqlHarnessGainSummary Total,
    SqlHarnessGainSummary Query,
    SqlHarnessGainSummary Compare)
{
    public SqlHarnessGainSummary Measure { get; init; } = Empty;
    public SqlHarnessGainSummary Ping { get; init; } = Empty;
    public SqlHarnessGainSummary Counts { get; init; } = Empty;
    public SqlHarnessGainSummary Space { get; init; } = Empty;
    public SqlHarnessGainSummary Watch { get; init; } = Empty;
    public SqlHarnessGainSummary Snapshot { get; init; } = Empty;
    public SqlHarnessGainSummary QueryStoreTop { get; init; } = Empty;
    public SqlHarnessGainSummary Indexes { get; init; } = Empty;
    internal static SqlHarnessGainSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>False when journal.enabled is off: the counts are zeros because nothing is recorded.</summary>
    public bool JournalEnabled { get; init; } = true;
}

/// <summary>Read side of the local output-savings statistics (the gain command).</summary>
internal interface IGainSource
{
    SqlHarnessGainReport Aggregate();
}