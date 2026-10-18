namespace SqlHarness.Core;

internal sealed record CompareMatrixRun(
    CompareCellRequest Template,
    IReadOnlyList<SqlHarnessParameter> FixedParameters,
    IReadOnlyList<ResolvedParameterSet> Sets,
    string? ParameterName,
    string? ParameterType);

/// <summary>One failed matrix cell. The inner exception is the original cell failure.</summary>
internal sealed class CompareMatrixCellFailedException : Exception
{
    internal int Index { get; }
    internal string? ParameterName { get; }
    internal string SetName { get; }
    internal CompareCellPhase Phase { get; }
    internal OutputFootprint RawFootprint { get; }
    internal SqlHarnessCompareMatrixReport? PartialReport { get; }
    internal IReadOnlyList<CompareRunArtifact> CompletedRuns { get; }

    internal MatrixExecutionCoverage Coverage { get; }

    internal CompareMatrixCellFailedException(
        int index,
        string? parameterName,
        string? parameterType,
        string setName,
        string? parameterValue,
        int requestedCount,
        CompareCellFailedException inner,
        IReadOnlyList<CompareMatrixCellReport> completedCells,
        IReadOnlyList<CompareRunArtifact> completedRuns,
        OutputFootprint rawFootprint)
        : base(null, Unwrap(inner))
    {
        Index = index;
        ParameterName = parameterName;
        SetName = setName;
        Phase = inner.Phase;
        RawFootprint = rawFootprint;
        Coverage = MatrixExecutionCoverage.IdentifiedFailure(
            requestedCount,
            completedCells.Count,
            FailedCase(index, parameterName, setName, parameterValue, inner.Phase));
        PartialReport = completedCells.Count == 0
            ? null
            : new SqlHarnessCompareMatrixReport(
                parameterName,
                parameterType,
                completedCells.ToArray())
            {
                Coverage = Coverage,
            };
        CompletedRuns = completedRuns.ToArray();
    }

    internal static MatrixFailedCase FailedCase(
        int index, string? parameterName, string setName, string? parameterValue, CompareCellPhase phase)
    {
        var namedSet = parameterName is null && !string.IsNullOrEmpty(setName);
        return new MatrixFailedCase(
            index,
            namedSet ? setName : null,
            namedSet ? null : parameterValue,
            PhaseName(phase))
        {
            IncludeParameterValue = !namedSet,
        };
    }

    internal static string? PhaseName(CompareCellPhase phase) => phase switch
    {
        CompareCellPhase.Authentication => "authentication",
        CompareCellPhase.Sql => "sql",
        CompareCellPhase.Artifact => "artifact",
        _ => null,
    };

    private static Exception Unwrap(CompareCellFailedException inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner.InnerException ?? inner;
    }
}

internal sealed record CompareMatrixResult(
    SqlHarnessCompareMatrixReport Report,
    OutputFootprint? RawFootprint)
{
    /// <summary>Every cell's measured runs, tagged with the zero-based cell index (never the value).</summary>
    internal IReadOnlyList<CompareRunArtifact> Runs { get; init; } = [];

    internal MatrixExecutionCoverage Coverage { get; init; } = MatrixExecutionCoverage.AllCompleted(0);
}

internal sealed class CompareMatrixRunner(CompareCellRunner cells)
{
    private readonly CompareCellRunner _cells = cells ?? throw new ArgumentNullException(nameof(cells));

    /// <summary>Copied onto the shared cell runner before each cell, same as a single compare.</summary>
    internal int ComparisonMaximumRows { get; set; } = CanonicalComparisonAccumulator.MaximumComparedRows;

    internal async Task<CompareMatrixResult> RunAsync(CompareMatrixRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var reports = new List<CompareMatrixCellReport>(run.Sets.Count);
        var runs = new List<CompareRunArtifact>();
        long bytes = 0;
        long lines = 0;
        var rawAvailable = true;
        // Sequential. The first failed cell throws and later values are not started.
        for (var index = 0; index < run.Sets.Count; index++)
        {
            var set = run.Sets[index];
            var request = run.Template with
            {
                Parameters = [.. run.FixedParameters, .. set.Parameters],
                ParameterInputs = [.. run.Template.ParameterInputs, .. set.Inputs],
                ConditionsCaseIndex = index,
            };
            _cells.ComparisonMaximumRows = ComparisonMaximumRows;
            try
            {
                var cell = await _cells.RunAsync(request, ct);
                if (cell.RawFootprint is { } footprint)
                {
                    bytes += footprint.Bytes;
                    lines += footprint.Lines;
                }
                else
                {
                    rawAvailable = false;
                }
                reports.Add(new CompareMatrixCellReport(index, set.ParameterValue, cell.Report)
                {
                    SetName = set.Name,
                    SetParameters = BenchmarkReports.ToParameterReports(set.Parameters),
                });
                var cellIndex = index;
                runs.AddRange(cell.Runs.Select(measured => measured with { MatrixCell = cellIndex }));
            }
            catch (CompareCellFailedException failed)
            {
                bytes += failed.RawFootprint.Bytes;
                lines += failed.RawFootprint.Lines;
                throw new CompareMatrixCellFailedException(
                    index,
                    run.ParameterName,
                    run.ParameterType,
                    set.Name,
                    set.ParameterValue,
                    run.Sets.Count,
                    failed,
                    reports,
                    runs,
                    new OutputFootprint(bytes, lines));
            }
        }

        var coverage = MatrixExecutionCoverage.AllCompleted(run.Sets.Count);
        return new CompareMatrixResult(
            new SqlHarnessCompareMatrixReport(run.ParameterName, run.ParameterType, reports)
            {
                Coverage = coverage,
            },
            rawAvailable ? new OutputFootprint(bytes, lines) : null)
        {
            Runs = runs,
            Coverage = coverage,
        };
    }
}
