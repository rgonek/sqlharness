using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal sealed record CompareCellRequest(
    ResolvedTarget Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<SqlHarnessParameter> Parameters,
    int TimeoutSeconds,
    int Repeat,
    ResultComparisonMode CompareResults)
{
    internal CompareClassificationReport Classification { get; init; } =
        new("none", "read-only", "read-only");
}

internal sealed record CompareCellResult(
    SqlHarnessCompareReport Report,
    IReadOnlyList<CompareRunArtifact> Runs,
    OutputFootprint RawFootprint);

internal enum CompareCellPhase
{
    Authentication,
    Sql,
    Artifact,
}

/// <summary>Cell failure with the phase and partial raw footprint still available.</summary>
internal sealed class CompareCellFailedException : Exception
{
    internal CompareCellPhase Phase { get; }
    internal OutputFootprint RawFootprint { get; }

    internal CompareCellFailedException(CompareCellPhase phase, OutputFootprint rawFootprint, Exception inner)
        : base(null, inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        Phase = phase;
        RawFootprint = rawFootprint;
    }
}

internal sealed class CompareCellRunner(ISqlSessionFactory sessions, ICompareArtifactWriter artifacts)
{
    private readonly ISqlSessionFactory _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly ICompareArtifactWriter _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));

    /// <summary>Copied from the module before each run. Defaults to the production row cap.</summary>
    internal int ComparisonMaximumRows { get; set; } = CanonicalComparisonAccumulator.MaximumComparedRows;

    internal async Task<CompareCellResult> RunAsync(CompareCellRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialect = SqlDialects.For(request.Target.Engine);
        CanonicalResultAccumulator? raw = null;
        var phase = CompareCellPhase.Authentication;
        try
        {
            await using var session = await _sessions.ConnectAsync(request.Target, ct);
            phase = CompareCellPhase.Sql;

            raw = new CanonicalResultAccumulator();
            if (!string.IsNullOrWhiteSpace(request.SetupSql))
            {
                await SqlHarnessModule.ExecuteRawAsync(
                    session,
                    new SqlExecutionCommand(request.SetupSql, request.Parameters, request.TimeoutSeconds),
                    raw,
                    ct);
            }

            // Fingerprints only for modes that run ResultComparer; off skips equivalence work entirely.
            // Warmup is EXPLAIN/STATISTICS only; sidecar follows measured repetitions.
            var captureComparison = request.CompareResults != ResultComparisonMode.Off;
            await ExecuteBenchmarkRunAsync(dialect, session, request.BaselineSql, request.Parameters, request.TimeoutSeconds, 0, "baseline", raw, captureComparison: false, ct);
            await ExecuteBenchmarkRunAsync(dialect, session, request.CandidateSql, request.Parameters, request.TimeoutSeconds, 0, "candidate", raw, captureComparison: false, ct);

            var runs = new List<CollectedCompareRun>(request.Repeat * 2);
            for (var repetition = 1; repetition <= request.Repeat; repetition++)
            {
                if (repetition % 2 == 1)
                {
                    runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, request.BaselineSql, request.Parameters, request.TimeoutSeconds, repetition, "baseline", raw, captureComparison, ct));
                    runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, request.CandidateSql, request.Parameters, request.TimeoutSeconds, repetition, "candidate", raw, captureComparison, ct));
                }
                else
                {
                    runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, request.CandidateSql, request.Parameters, request.TimeoutSeconds, repetition, "candidate", raw, captureComparison, ct));
                    runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, request.BaselineSql, request.Parameters, request.TimeoutSeconds, repetition, "baseline", raw, captureComparison, ct));
                }
            }

            var rawFootprint = raw.Complete().Footprint;
            var baselineRuns = runs.Where(run => run.Variant == "baseline").ToArray();
            var candidateRuns = runs.Where(run => run.Variant == "candidate").ToArray();
            var equivalence = ResultComparer.Compare(
                request.CompareResults,
                baselineRuns.Select(run => run.Comparison).ToArray(),
                candidateRuns.Select(run => run.Comparison).ToArray());
            var report = new SqlHarnessCompareReport(
                session.Identity,
                request.Repeat,
                runs.Count,
                equivalence.Equivalent,
                SqlHarnessModule.CreateVariantReport("baseline", baselineRuns),
                SqlHarnessModule.CreateVariantReport("candidate", candidateRuns),
                null)
            {
                Equivalence = equivalence,
                Classification = request.Classification,
                Parameters = SqlHarnessModule.ToParameterReports(request.Parameters),
            };

            phase = CompareCellPhase.Artifact;
            var publicRuns = runs.Select(run => run.Artifact).ToArray();
            var directory = _artifacts.Write(report, publicRuns, request.Target.Database);
            report = report with { ArtifactDirectory = directory };
            return new CompareCellResult(report, publicRuns, rawFootprint);
        }
        catch (Exception exception) when (exception is not CompareCellFailedException)
        {
            var footprint = raw is null ? new OutputFootprint(0, 0) : raw.SnapshotFootprint();
            throw new CompareCellFailedException(phase, footprint, exception);
        }
        finally
        {
            raw?.Dispose();
        }
    }

    private Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlDialect dialect,
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator raw,
        bool captureComparison,
        CancellationToken ct) =>
        dialect.ExecuteBenchmarkRunAsync(
            session,
            sql,
            parameters,
            timeoutSeconds,
            repetition,
            variant,
            raw,
            captureComparison,
            ComparisonMaximumRows,
            ct);
}