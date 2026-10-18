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
    IReadOnlySet<string> SetupTempTables)
{
    internal SqlSafetyDecision? SetupSafety { get; init; }
    internal SqlRiskTier Tier { get; init; }
    internal IReadOnlyList<string> SafetyNotices { get; init; } = [];
    internal IReadOnlyList<SqlHarnessParameter> ClassificationParameters { get; init; } = FixedParameters;
    internal string? ClassificationSetupSql { get; init; }
    internal required string ClassificationBaselineSql { get; init; }
    internal required string ClassificationCandidateSql { get; init; }
    internal bool HasNonScalarParameters { get; init; }
}

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
        OperationSecrets.AddCredentials(knownSecrets, target);
        var dialect = SqlDialects.For(target.Engine);

        var resolvedParameterInputs = SqlParameterInputs.Resolve(parameterInputs, typedParameters).ToArray();
        SqlParameterInputs.ValidateSourceSql(dialect, target.Database, resolvedParameterInputs, NoSessionTemps);
        var tableValuedNames = ParameterSetResolver.ValidateInputs(resolvedParameterInputs, target.Engine);
        var scalarParameters = dialect.BindParameters(
            resolvedParameterInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
        var baselineExpansion = SqlParameterInputs.ExpandListsForClassification(baselineSql, resolvedParameterInputs);
        var candidateExpansion = SqlParameterInputs.ExpandListsForClassification(candidateSql, resolvedParameterInputs);
        var setupExpansion = string.IsNullOrWhiteSpace(setupSql)
            ? new ExpandedSql(setupSql ?? string.Empty, [])
            : SqlParameterInputs.ExpandListsForClassification(setupSql, resolvedParameterInputs);
        var classificationParameters = SqlParameterInputs.CombineClassificationParameters(scalarParameters, baselineExpansion.Parameters);

        SqlSafetyDecision? setupSafety = null;
        IReadOnlySet<string> setupTemps = NoSessionTemps;
        if (!string.IsNullOrWhiteSpace(setupSql))
        {
            setupSafety = dialect.Classify(
                setupExpansion.Text, SqlUsage.CompareSetup, target.Database, false, null, NoSessionTemps, tableValuedNames);
            EnsureSafe(setupSafety, "setup");
            setupTemps = setupSafety.SessionTempTables;
        }

        var baselineSafety = dialect.Classify(
            baselineExpansion.Text, SqlUsage.Query, target.Database, false, null, setupTemps, tableValuedNames);
        EnsureSafe(baselineSafety, "baseline");
        var candidateSafety = dialect.Classify(
            candidateExpansion.Text, SqlUsage.Query, target.Database, false, null, setupTemps, tableValuedNames);
        EnsureSafe(candidateSafety, "candidate");

        var parameters = scalarParameters;

        return new PreparedCompareFamily(
            target,
            dialect,
            parameters,
            new CompareClassificationReport(
                BenchmarkReports.ClassificationLabel(setupSafety),
                BenchmarkReports.ClassificationLabel(baselineSafety),
                BenchmarkReports.ClassificationLabel(candidateSafety)),
            setupTemps)
        {
            SetupSafety = setupSafety,
            SafetyNotices = (setupSafety?.Notices ?? [])
                .Concat(baselineSafety.Notices)
                .Concat(candidateSafety.Notices)
                .Distinct(StringComparer.Ordinal).ToArray(),
            Tier = SqlRiskTiers.Max(
                setupSafety?.Tier ?? SqlRiskTier.ReadOnly,
                SqlRiskTiers.Max(baselineSafety.Tier, candidateSafety.Tier)),
            ClassificationParameters = classificationParameters,
            ClassificationSetupSql = string.IsNullOrWhiteSpace(setupSql) ? setupSql : setupExpansion.Text,
            ClassificationBaselineSql = baselineExpansion.Text,
            ClassificationCandidateSql = candidateExpansion.Text,
            HasNonScalarParameters = resolvedParameterInputs.Any(input => ParameterKindParser.Parse(input) != ParameterKind.Scalar),
        };
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
            var validationParameters = prepared.HasNonScalarParameters
                ? SqlParameterInputs.CombineClassificationParameters(
                    variant,
                    prepared.ClassificationParameters.Where(parameter => !prepared.FixedParameters.Any(scalar => scalar.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))).ToArray())
                : variant;
            if (prepared.Dialect.Engine == SqlEngine.SqlServer)
                SqlSetupVariableReferenceValidator.Validate(prepared.ClassificationSetupSql, validationParameters, prepared.ClassificationBaselineSql, prepared.ClassificationCandidateSql);
            prepared.Dialect.ValidateParameterReferences(validationParameters, prepared.ClassificationSetupSql, prepared.ClassificationBaselineSql, prepared.ClassificationCandidateSql);
            SetupSqlExecution.Validate(prepared.Dialect.Engine, prepared.ClassificationSetupSql, validationParameters);
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
    ResultComparisonMode CompareResults,
    bool CompareNullability = false)
{
    internal CompareClassificationReport Classification { get; init; } =
        new("none", "read-only", "read-only");

    internal ArtifactOwner? Owner { get; init; }

    internal string? OperationRef { get; init; }

    internal SqlSafetyDecision? SetupSafety { get; init; }

    internal IReadOnlyList<string> SafetyNotices { get; init; } = [];

    internal List<string> KnownSecrets { get; init; } = [];
}

internal sealed record CompareCellResult(
    SqlHarnessCompareReport Report,
    IReadOnlyList<CompareRunArtifact> Runs,
    OutputFootprint? RawFootprint);

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
            await using var session = await _sessions.ConnectAsync(request.Target, ct, request.KnownSecrets);
            phase = CompareCellPhase.Sql;

            raw = new CanonicalResultAccumulator();
            var setupWarnings = await SetupSqlExecution.ExecuteSetupAsync(
                request.Target.Engine,
                request.SetupSql,
                request.Parameters,
                request.TimeoutSeconds,
                session,
                raw: null,
                ct,
                request.SetupSafety);
            var sessionState = await dialect.CaptureSessionStateAsync(session, ct);

            // Fingerprints only for modes that run ResultComparer; off skips equivalence work entirely.
            // Warmup is EXPLAIN/STATISTICS only; sidecar follows measured repetitions.
            var captureComparison = request.CompareResults != ResultComparisonMode.Off;
            await ExecuteBenchmarkRunAsync(dialect, session, request.BaselineSql, request.Parameters, request.TimeoutSeconds, 0, "baseline", raw: null, captureComparison: false, sessionState, ct);
            await ExecuteBenchmarkRunAsync(dialect, session, request.CandidateSql, request.Parameters, request.TimeoutSeconds, 0, "candidate", raw: null, captureComparison: false, sessionState, ct);

            var runs = new List<CollectedBenchmarkRun>(request.Repeat * 2);
            for (var repetition = 1; repetition <= request.Repeat; repetition++)
            {
                if (repetition % 2 == 1)
                {
                    runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, request.BaselineSql, request.Parameters, request.TimeoutSeconds, repetition, "baseline", repetition == 1 ? raw : null, captureComparison, sessionState, ct));
                    runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, request.CandidateSql, request.Parameters, request.TimeoutSeconds, repetition, "candidate", repetition == 1 ? raw : null, captureComparison, sessionState, ct));
                }
                else
                {
                    runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, request.CandidateSql, request.Parameters, request.TimeoutSeconds, repetition, "candidate", repetition == 1 ? raw : null, captureComparison, sessionState, ct));
                    runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, request.BaselineSql, request.Parameters, request.TimeoutSeconds, repetition, "baseline", repetition == 1 ? raw : null, captureComparison, sessionState, ct));
                }
            }

            var rawFootprint = raw.Complete().Footprint;
            if (request.Target.Engine == SqlEngine.Postgres && request.CompareResults == ResultComparisonMode.Off)
                rawFootprint = null;
            var baselineRuns = runs.Where(run => run.Variant == "baseline").ToArray();
            var candidateRuns = runs.Where(run => run.Variant == "candidate").ToArray();
            var equivalence = ResultComparer.Compare(
                request.CompareResults,
                baselineRuns.Select(run => run.Comparison).ToArray(),
                candidateRuns.Select(run => run.Comparison).ToArray(),
                ct,
                request.CompareNullability);
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
                Warnings = setupWarnings
                    .Concat(request.SafetyNotices)
                    .Concat(baselineRuns.SelectMany(run => run.Warnings))
                    .Concat(candidateRuns.SelectMany(run => run.Warnings))
                    .Distinct(StringComparer.Ordinal).ToArray(),
                Regression = RegressionPolicy.Evaluate(new RegressionInput(
                    request.Target.Engine,
                    ToRegressionVariant(baselineRuns),
                    ToRegressionVariant(candidateRuns),
                    equivalence)),
            };

            // Still the measured-SQL phase: a catalog failure stays a gap and cancellation maps like a measured batch.
            var inventory = await IndexInventoryCapture.CaptureAsync(
                request.Target.Engine,
                session,
                runs.SelectMany(run => run.Artifact.PlanXmls).ToArray(),
                request.TimeoutSeconds,
                request.KnownSecrets,
                ct);
            phase = CompareCellPhase.Artifact;
            var publicRuns = runs.Select(run => run.Artifact).ToArray();
            var directory = _artifacts.Write(
                report,
                publicRuns,
                request.Target.Database,
                request.Owner,
                request.OperationRef,
                inventory);
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

    // Measured repetitions only (warm-up is never in runs). Metrics are null for whole-millisecond runs.
    private static RegressionVariantInput ToRegressionVariant(IReadOnlyList<CollectedBenchmarkRun> variantRuns)
    {
        var artifacts = variantRuns.Select(run => run.Artifact).ToArray();
        static string Availability(IEnumerable<string?> tokens) =>
            tokens.All(token => token is null || string.Equals(token, BenchmarkMetricReport.Measured, StringComparison.Ordinal))
                ? BenchmarkMetricReport.Measured
                : BenchmarkMetricReport.Unavailable;

        return new RegressionVariantInput(
            artifacts.Select(run => run.Metrics?.ElapsedTimeMillisecondsExact ?? run.ElapsedTimeMilliseconds).ToArray(),
            artifacts.Select(run => run.LogicalReads).ToArray(),
            artifacts.Select(run => run.CpuTimeMilliseconds).ToArray(),
            Availability(artifacts.Select(run => run.Metrics?.ElapsedTimeAvailability)),
            Availability(artifacts.Select(run => run.Metrics?.LogicalReadsAvailability)),
            Availability(artifacts.Select(run => run.Metrics?.CpuTimeAvailability)));
    }

    private Task<CollectedBenchmarkRun> ExecuteBenchmarkRunAsync(
        ISqlDialect dialect,
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator? raw,
        bool captureComparison,
        SessionState? sessionState,
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
            ct,
            sessionState);
}
