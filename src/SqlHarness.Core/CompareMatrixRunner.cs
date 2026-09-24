namespace SqlHarness.Core;

internal sealed record CompareMatrixRun(
    CompareCellRequest Template,
    IReadOnlyList<SqlHarnessParameter> FixedParameters,
    IReadOnlyList<SqlHarnessParameter> MatrixValues,
    IReadOnlyList<string> DisplayValues,
    string ParameterName,
    string ParameterType);

/// <summary>One failed matrix cell. The inner exception is the original cell failure.</summary>
internal sealed class CompareMatrixCellFailedException : Exception
{
    internal int Index { get; }
    internal string ParameterName { get; }
    internal CompareCellPhase Phase { get; }
    internal OutputFootprint RawFootprint { get; }

    internal CompareMatrixCellFailedException(int index, string parameterName, CompareCellFailedException inner)
        : base(null, Unwrap(inner))
    {
        Index = index;
        ParameterName = parameterName ?? throw new ArgumentNullException(nameof(parameterName));
        Phase = inner.Phase;
        RawFootprint = inner.RawFootprint;
    }

    private static Exception Unwrap(CompareCellFailedException inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner.InnerException ?? inner;
    }
}

internal sealed record CompareMatrixResult(
    SqlHarnessCompareMatrixReport Report,
    OutputFootprint RawFootprint);

internal sealed class CompareMatrixRunner(CompareCellRunner cells)
{
    private readonly CompareCellRunner _cells = cells ?? throw new ArgumentNullException(nameof(cells));

    /// <summary>Copied onto the shared cell runner before each cell, same as a single compare.</summary>
    internal int ComparisonMaximumRows { get; set; } = CanonicalComparisonAccumulator.MaximumComparedRows;

    internal async Task<CompareMatrixResult> RunAsync(CompareMatrixRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var reports = new List<CompareMatrixCellReport>(run.MatrixValues.Count);
        long bytes = 0;
        long lines = 0;
        // Sequential. The first failed cell throws and later values are not started.
        for (var index = 0; index < run.MatrixValues.Count; index++)
        {
            var request = run.Template with
            {
                Parameters = [.. run.FixedParameters, run.MatrixValues[index]],
            };
            _cells.ComparisonMaximumRows = ComparisonMaximumRows;
            try
            {
                var cell = await _cells.RunAsync(request, ct);
                bytes += cell.RawFootprint.Bytes;
                lines += cell.RawFootprint.Lines;
                reports.Add(new CompareMatrixCellReport(index, run.DisplayValues[index], cell.Report));
            }
            catch (CompareCellFailedException failed)
            {
                throw new CompareMatrixCellFailedException(index, run.ParameterName, failed);
            }
        }

        return new CompareMatrixResult(
            new SqlHarnessCompareMatrixReport(run.ParameterName, run.ParameterType, reports),
            new OutputFootprint(bytes, lines));
    }
}