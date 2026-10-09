using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

/// <summary>
/// Shared preparation for the compare family (single compare and compare
/// matrix): bounds, target resolution, safety classification of setup /
/// baseline / candidate, fixed-parameter parsing and measured-batch
/// validation. Matrix-only concerns (matrix parsing, per-value binding, fresh
/// connection per value) stay with the matrix dispatch.
/// </summary>
internal sealed record PreparedCompareFamily(
    ResolvedTarget Target,
    ISqlDialect Dialect,
    IReadOnlyList<SqlHarnessParameter> FixedParameters,
    CompareClassificationReport Classification,
    IReadOnlySet<string> SetupTempTables);

internal static class CompareOperationPreparer
{
    private static readonly IReadOnlySet<string> NoSessionTemps =
        new HashSet<string>(StringComparer.Ordinal);

    internal static void EnsureSafe(SqlSafetyDecision decision, string label)
    {
        if (!decision.Allowed)
            throw new SqlHarnessSafetyException($"SQL safety rejection for {label}: {decision.RejectionDescription}");
    }

    internal static PreparedCompareFamily PrepareFixed(
        SqlTargetRequest targetRequest,
        IReadOnlyDictionary<string, TargetProfile> profiles,
        string? setupSql,
        string baselineSql,
        string candidateSql,
        IReadOnlyList<string> parameterInputs,
        int timeoutSeconds,
        int repeat,
        List<string> knownSecrets,
        IReadOnlyList<SqlHarnessParameterInput>? typedParameters = null)
    {
        ArgumentNullException.ThrowIfNull(targetRequest);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(baselineSql);
        ArgumentNullException.ThrowIfNull(candidateSql);
        ArgumentNullException.ThrowIfNull(parameterInputs);
        ArgumentNullException.ThrowIfNull(knownSecrets);

        if (timeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (repeat is < 1 or > 100)
            throw new SqlHarnessSafetyException("Compare repetitions must be between 1 and 100.");

        var target = TargetResolver.Resolve(targetRequest, profiles);
        var dialect = SqlDialects.For(target.Engine);

        SqlSafetyDecision? setupSafety = null;
        IReadOnlySet<string> setupTemps = NoSessionTemps;
        if (!string.IsNullOrWhiteSpace(setupSql))
        {
            setupSafety = dialect.Classify(
                setupSql, SqlUsage.CompareSetup, target.Database, false, null, NoSessionTemps);
            EnsureSafe(setupSafety, "setup");
            setupTemps = setupSafety.SessionTempTables;
        }

        var baselineSafety = dialect.Classify(
            baselineSql, SqlUsage.Query, target.Database, false, null, setupTemps);
        EnsureSafe(baselineSafety, "baseline");
        var candidateSafety = dialect.Classify(
            candidateSql, SqlUsage.Query, target.Database, false, null, setupTemps);
        EnsureSafe(candidateSafety, "candidate");

        SqlParameterSecrets.AddValues(knownSecrets, parameterInputs);
        var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(parameterInputs, typedParameters));

        return new PreparedCompareFamily(
            target,
            dialect,
            parameters,
            new CompareClassificationReport(
                BenchmarkReports.ClassificationLabel(setupSafety),
                BenchmarkReports.ClassificationLabel(baselineSafety),
                BenchmarkReports.ClassificationLabel(candidateSafety)),
            setupTemps);
    }

    internal static void ValidateVariants(
        PreparedCompareFamily prepared,
        string? setupSql,
        string baselineSql,
        string candidateSql,
        IReadOnlyList<IReadOnlyList<SqlHarnessParameter>> variants)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(variants);
        foreach (var variant in variants)
        {
            if (prepared.Dialect.Engine == SqlEngine.SqlServer)
                SqlSetupVariableReferenceValidator.Validate(setupSql, variant, baselineSql, candidateSql);
            prepared.Dialect.ValidateParameterReferences(variant, setupSql, baselineSql, candidateSql);
            SetupSqlExecution.Validate(prepared.Dialect.Engine, setupSql, variant);
        }

        prepared.Dialect.ValidateMeasuredBatch(baselineSql);
        prepared.Dialect.ValidateMeasuredBatch(candidateSql);
    }
}

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

    internal ArtifactOwner? Owner { get; init; }
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
            await SetupSqlExecution.ExecuteSetupAsync(
                request.Target.Engine,
                request.SetupSql,
                request.Parameters,
                request.TimeoutSeconds,
                session,
                raw,
                ct);

            // Fingerprints only for modes that run ResultComparer; off skips equivalence work entirely.
            // Warmup is EXPLAIN/STATISTICS only; sidecar follows measured repetitions.
            var captureComparison = request.CompareResults != ResultComparisonMode.Off;
            await ExecuteBenchmarkRunAsync(dialect, session, request.BaselineSql, request.Parameters, request.TimeoutSeconds, 0, "baseline", raw, captureComparison: false, ct);
            await ExecuteBenchmarkRunAsync(dialect, session, request.CandidateSql, request.Parameters, request.TimeoutSeconds, 0, "candidate", raw, captureComparison: false, ct);

            var runs = new List<CollectedBenchmarkRun>(request.Repeat * 2);
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
                candidateRuns.Select(run => run.Comparison).ToArray(),
                ct);
            var report = new SqlHarnessCompareReport(
                session.Identity,
                request.Repeat,
                runs.Count,
                equivalence.Equivalent,
                BenchmarkReports.CreateVariantReport("baseline", baselineRuns),
                BenchmarkReports.CreateVariantReport("candidate", candidateRuns),
                null)
            {
                Equivalence = equivalence,
                Classification = request.Classification,
                Parameters = BenchmarkReports.ToParameterReports(request.Parameters),
            };

            phase = CompareCellPhase.Artifact;
            var publicRuns = runs.Select(run => run.Artifact).ToArray();
            var directory = _artifacts.Write(report, publicRuns, request.Target.Database, request.Owner);
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

    private Task<CollectedBenchmarkRun> ExecuteBenchmarkRunAsync(
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
        BenchmarkRunner.ExecuteAsync(
            dialect,
            session,
            sql,
            parameters,
            timeoutSeconds,
            repetition,
            variant,
            parameterSet: null,
            raw,
            captureComparison,
            ComparisonMaximumRows,
            ct);
}