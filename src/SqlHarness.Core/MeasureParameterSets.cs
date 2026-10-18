using System.Data;
using System.Data.SqlTypes;
using System.Security.Cryptography;
using System.Text;

using Microsoft.SqlServer.Types;

using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

public sealed record MeasureParameterMetadata(string Name, string Type);

internal sealed record PreparedMeasureParameterSet(
    string Name,
    IReadOnlyList<SqlHarnessParameter> Parameters,
    IReadOnlyList<MeasureParameterMetadata> Metadata,
    string? ValueHash)
{
    internal IReadOnlyList<SqlHarnessParameterInput> Inputs { get; init; } = [];
    internal string? ExecutionSql { get; init; }
}

internal static class MeasureParameterSetValidator
{
    internal static IReadOnlyList<PreparedMeasureParameterSet> Prepare(
        IReadOnlyList<string> fixedParameters,
        IReadOnlyList<SqlHarnessParameterSetInput> parameterSets,
        string? setupSql,
        string querySql,
        ISqlDialect? dialect = null,
        IReadOnlyList<SqlHarnessParameterInput>? typedFixedParameters = null)
    {
        ArgumentNullException.ThrowIfNull(fixedParameters);
        ArgumentNullException.ThrowIfNull(parameterSets);
        ArgumentNullException.ThrowIfNull(querySql);
        dialect ??= SqlDialects.For(SqlEngine.SqlServer);

        var fixedInputs = SqlParameterInputs.Resolve(fixedParameters, typedFixedParameters).ToArray();
        var fixedBound = dialect.BindParameters(fixedInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
        // The resolver owns shared set validation and binding; this class adds measure-only
        // reference checks and metadata/hash projection over the resolved execution parameters.
        var resolvedSets = ParameterSetResolver.Resolve(dialect, fixedBound, null, parameterSets);
        var prepared = new List<PreparedMeasureParameterSet>(parameterSets.Count);

        foreach (var set in resolvedSets)
        {
            var parameters = fixedBound.Concat(set.Parameters).ToArray();
            var inputs = fixedInputs.Concat(set.Inputs)
                .ToArray();
            ParameterSetResolver.ValidateInputs(inputs, dialect.Engine);
            var orderedParameters = Order(parameters);
            var queryExpansion = SqlParameterInputs.ExpandListsForClassification(querySql, inputs);
            var setupExpansion = string.IsNullOrWhiteSpace(setupSql)
                ? new ExpandedSql(setupSql ?? string.Empty, [])
                : SqlParameterInputs.ExpandListsForClassification(setupSql, inputs);
            var validationParameters = SqlParameterInputs.CombineClassificationParameters(
                SqlParameterInputs.CombineClassificationParameters(parameters, setupExpansion.Parameters),
                queryExpansion.Parameters);
            if (dialect.Engine == SqlEngine.SqlServer)
                SqlSetupVariableReferenceValidator.Validate(setupExpansion.Text, validationParameters, queryExpansion.Text);
            dialect.ValidateParameterReferences(validationParameters, setupExpansion.Text, queryExpansion.Text);
            SetupSqlExecution.Validate(dialect.Engine, setupExpansion.Text, validationParameters);
            prepared.Add(new PreparedMeasureParameterSet(
                set.Name,
                parameters,
                ToMetadata(orderedParameters),
                inputs.Any(input => ParameterKindParser.Parse(input) != ParameterKind.Scalar) ? null : TypedParameterHasher.Hash(parameters))
            {
                Inputs = inputs,
            });
        }

        return prepared;
    }

    private static SqlHarnessParameter[] Order(IEnumerable<SqlHarnessParameter> parameters) =>
        parameters
            .OrderBy(parameter => parameter.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToArray();

    private static MeasureParameterMetadata[] ToMetadata(IReadOnlyList<SqlHarnessParameter> parameters) =>
        parameters
            .Select(parameter => new MeasureParameterMetadata(parameter.Name, MeasureParameterTyping.TypeName(parameter)))
            .ToArray();

}

internal static class TypedParameterHasher
{
    internal static string Hash(IReadOnlyList<SqlHarnessParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var ordered = parameters
            .OrderBy(parameter => parameter.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToArray();

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            // Versioned little-endian record. Names are lower-invariant so culture and input order cannot change the digest.
            writer.Write((byte)1);
            writer.Write(ordered.Length);
            foreach (var parameter in ordered)
                WriteParameter(writer, parameter);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteParameter(BinaryWriter writer, SqlHarnessParameter parameter)
    {
        WriteUtf8(writer, parameter.Name.ToLowerInvariant());
        WriteUtf8(writer, MeasureParameterTyping.TypeName(parameter));
        WriteOptional(writer, parameter.Size);
        WriteOptional(writer, parameter.Precision);
        WriteOptional(writer, parameter.Scale);
        var isNull = IsNull(parameter.Value);
        writer.Write(isNull);
        if (!isNull)
            WriteValue(writer, parameter.Value);
    }

    private static void WriteOptional(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value is int present)
            writer.Write(present);
    }

    private static void WriteOptional(BinaryWriter writer, byte? value)
    {
        writer.Write(value.HasValue);
        if (value is byte present)
            writer.Write(present);
    }

    private static bool IsNull(object? value) => value switch
    {
        null or DBNull => true,
        INullable nullable => nullable.IsNull,
        _ => false,
    };

    private static void WriteValue(BinaryWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                WriteUtf8(writer, text);
                break;
            case bool bit:
                writer.Write(bit);
                break;
            case byte tiny:
                writer.Write(tiny);
                break;
            case short small:
                writer.Write(small);
                break;
            case int number:
                writer.Write(number);
                break;
            case long big:
                writer.Write(big);
                break;
            case float real:
                writer.Write(BitConverter.SingleToInt32Bits(real));
                break;
            case double floating:
                writer.Write(BitConverter.DoubleToInt64Bits(floating));
                break;
            case decimal number:
                writer.Write(CanonicalDecimal(number));
                break;
            case DateTime dateTime:
                writer.Write(dateTime.Ticks);
                writer.Write((byte)dateTime.Kind);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.Write(dateTimeOffset.Ticks);
                writer.Write(dateTimeOffset.Offset.Ticks);
                break;
            case TimeSpan time:
                writer.Write(time.Ticks);
                break;
            case Guid guid:
                writer.Write(guid.ToByteArray());
                break;
            case byte[] bytes:
                writer.Write(bytes.Length);
                writer.Write(bytes);
                break;
            case SqlHierarchyId hierarchy:
                WriteNative(writer, hierarchy.Write);
                break;
            case SqlGeography geography:
                WriteNative(writer, geography.Write);
                break;
            case SqlGeometry geometry:
                WriteNative(writer, geometry.Write);
                break;
            default:
                throw new SqlHarnessSafetyException("SQL parameter value cannot be hashed.");
        }
    }

    // Trailing fractional zeros are not a distinct typed value. Declared precision and scale are written separately.
    private static decimal CanonicalDecimal(decimal value)
    {
        if (value == 0m)
            return 0m;

        while (true)
        {
            var scale = (decimal.GetBits(value)[3] >> 16) & 0x7F;
            if (scale == 0)
                return value;

            var reduced = decimal.Round(value, scale - 1, MidpointRounding.ToZero);
            var reducedScale = (decimal.GetBits(reduced)[3] >> 16) & 0x7F;
            if (reduced != value || reducedScale >= scale)
                return value;

            value = reduced;
        }
    }

    private static void WriteNative(BinaryWriter writer, Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var inner = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            write(inner);

        var bytes = buffer.ToArray();
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteUtf8(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}

file static class MeasureParameterTyping
{
    public static string TypeName(SqlHarnessParameter parameter) =>
        parameter.Type == SqlDbType.Udt && !string.IsNullOrEmpty(parameter.UdtTypeName)
            ? parameter.UdtTypeName.ToLowerInvariant()
            : parameter.Type.ToString().ToLowerInvariant();
}

internal sealed record MeasureParameterSetExecution(
    int SetupExecutionCount,
    IReadOnlyList<string> WarmupOrder,
    IReadOnlyList<CollectedBenchmarkRun> Runs,
    int CompletedCases = 0,
    MeasureCaseFailure? FailedCase = null)
{
    internal ISqlSession? FinalSession { get; init; }
    internal IReadOnlyList<string> Warnings { get; init; } = [];
}

internal sealed record MeasureCaseFailure(int Index, string SetName, string? Phase);

internal sealed class MeasureCaseFailedException : Exception
{
    internal IReadOnlyList<CollectedBenchmarkRun> CompletedRuns { get; }
    internal SqlHarnessMeasureSetReport? PartialReport { get; }
    internal MeasureParameterSetExecution Execution { get; }
    internal int RequestedCases { get; }

    internal MeasureCaseFailedException(
        Exception inner,
        IReadOnlyList<CollectedBenchmarkRun> completedRuns,
        SqlHarnessMeasureSetReport? partialReport,
        MeasureParameterSetExecution execution,
        int requestedCases)
        : base(inner.Message, inner)
    {
        CompletedRuns = completedRuns.ToArray();
        PartialReport = partialReport;
        Execution = execution;
        RequestedCases = requestedCases;
    }
}

internal sealed class MeasureParameterSetRunner
{
    private readonly ISqlDialect _dialect;
    private readonly ISqlSessionFactory _sessions;
    private readonly int _comparisonMaximumRows;

    internal MeasureParameterSetRunner(ISqlDialect dialect, ISqlSessionFactory sessions, int comparisonMaximumRows)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        ArgumentOutOfRangeException.ThrowIfNegative(comparisonMaximumRows);
        _comparisonMaximumRows = comparisonMaximumRows;
    }

    internal async Task<MeasureParameterSetExecution> ExecuteAsync(
        ResolvedTarget target,
        SqlHarnessMeasureOperation operation,
        IReadOnlyList<PreparedMeasureParameterSet> sets,
        CanonicalResultAccumulator raw,
        CancellationToken ct,
        SqlSafetyDecision? setupSafety = null,
        List<string>? knownSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(raw);

        var setupExecutionCount = 0;
        var warmupOrder = new List<string>(sets.Count);
        var runs = new List<CollectedBenchmarkRun>(operation.Repeat * sets.Count);
        var completedCases = 0;
        var setupWarnings = new List<string>();
        SqlHarnessTargetIdentityReport? identity = null;
        ISqlSession? finalSession = null;
        for (var index = 0; index < sets.Count; index++)
        {
            var set = sets[index];
            ISqlSession? session = null;
            var phase = "authentication";
            Exception? failure = null;
            var caseRuns = new List<CollectedBenchmarkRun>(operation.Repeat);
            try
            {
                session = await _sessions.ConnectAsync(target, ct, knownSecrets ?? []);
                identity ??= session.Identity;
                phase = "sql";
                var materialized = await TableParameterMaterializer.MaterializeAsync(session, set.Inputs, _dialect, ct);
                var structuredParameters = materialized.Parameters.Where(parameter => parameter.Type == SqlDbType.Structured).ToArray();
                var setupExpansion = string.IsNullOrWhiteSpace(operation.SetupSql) ? null : materialized.Expand(operation.SetupSql);
                var queryExpansion = materialized.Expand(operation.QuerySql);
                var setupParameters = setupExpansion is null
                    ? set.Parameters.Concat(structuredParameters).ToArray()
                    : SqlParameterInputs.CombineClassificationParameters(set.Parameters.Concat(structuredParameters).ToArray(), setupExpansion.Parameters);
                var queryParameters = SqlParameterInputs.CombineClassificationParameters(set.Parameters.Concat(structuredParameters).ToArray(), queryExpansion.Parameters);
                if (setupParameters.Count > ParameterSetResolver.MaximumCommandParameters || queryParameters.Count > ParameterSetResolver.MaximumCommandParameters)
                    throw new SqlHarnessSafetyException($"SQL parameter list exceeds the {ParameterSetResolver.MaximumCommandParameters} final command parameter limit.");
                var executionSet = set with { Parameters = queryParameters, ExecutionSql = queryExpansion.Text };
                if (!string.IsNullOrWhiteSpace(operation.SetupSql))
                {
                    setupWarnings.AddRange(await SetupSqlExecution.ExecuteSetupAsync(
                        _dialect.Engine,
                        setupExpansion!.Text,
                        setupParameters,
                        operation.TimeoutSeconds,
                        session,
                        raw: null,
                        ct,
                        setupSafety));
                    setupExecutionCount++;
                }

                var sessionState = await _dialect.CaptureSessionStateAsync(session, ct);
                await ExecuteSetAsync(session, executionSet, repetition: 0, sessionState);
                for (var repetition = 1; repetition <= operation.Repeat; repetition++)
                    caseRuns.Add(await ExecuteSetAsync(session, executionSet, repetition, sessionState));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (session is not null && (failure is not null || index < sets.Count - 1))
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception) when (failure is not null)
                {
                    // Keep the case failure (including cancellation) as the primary outcome.
                }
                catch (Exception cleanupFailure)
                {
                    failure = cleanupFailure;
                }
            }

            if (failure is not null)
            {
                if (failure is OperationCanceledException && ct.IsCancellationRequested)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

                var failedCase = new MeasureCaseFailure(index, set.Name, phase);
                var execution = new MeasureParameterSetExecution(
                    setupExecutionCount, warmupOrder, runs, completedCases, failedCase)
                {
                    Warnings = setupWarnings.Concat(runs.SelectMany(run => run.Warnings))
                        .Distinct(StringComparer.Ordinal).ToArray(),
                };
                var partialReport = completedCases == 0 || identity is null
                    ? null
                    : MeasureParameterSetReportProjector.Project(
                        identity,
                        operation.Repeat,
                        execution,
                        sets.Take(completedCases).ToArray());
                throw new MeasureCaseFailedException(failure, runs, partialReport, execution, sets.Count);
            }

            if (index == sets.Count - 1)
                finalSession = session;
            runs.AddRange(caseRuns);
            warmupOrder.Add(set.Name);
            completedCases++;
        }

        return new MeasureParameterSetExecution(setupExecutionCount, warmupOrder, runs, completedCases)
        {
            FinalSession = finalSession,
            Warnings = setupWarnings.Concat(runs.SelectMany(run => run.Warnings))
                .Distinct(StringComparer.Ordinal).ToArray(),
        };

        Task<CollectedBenchmarkRun> ExecuteSetAsync(
            ISqlSession session,
            PreparedMeasureParameterSet set,
            int repetition,
            SessionState? sessionState = null) =>
            BenchmarkRunner.ExecuteAsync(
                _dialect,
                session,
                set.ExecutionSql ?? operation.QuerySql,
                set.Parameters,
                operation.TimeoutSeconds,
                repetition,
                "measure",
                set.Name,
                repetition == 1 ? raw : null,
                captureComparison: false,
                _comparisonMaximumRows,
                ct,
                sessionState);
    }
}

internal static class MeasureParameterSetReportProjector
{
    internal const string MeasuredOrderRule =
        "Measured runs execute parameter sets sequentially in user-supplied order.";

    internal const string PlanCacheWarning =
        "Parameter-set measurements use separate sessions; SQLHarness did not clear or isolate the server plan cache.";

    internal static SqlHarnessMeasureSetReport Project(
        SqlHarnessTargetIdentityReport target,
        int repeat,
        MeasureParameterSetExecution execution,
        IReadOnlyList<PreparedMeasureParameterSet> sets)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(execution.Runs);
        ArgumentNullException.ThrowIfNull(execution.WarmupOrder);
        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Count == 0)
            throw new ArgumentException("At least one parameter set is required.", nameof(sets));

        if (execution.CompletedCases > 0 && execution.FailedCase is not null && execution.CompletedCases < sets.Count)
            sets = sets.Take(execution.CompletedCases).ToArray();

        var setReports = new MeasureParameterSetReport[sets.Count];
        for (var index = 0; index < sets.Count; index++)
            setReports[index] = ProjectSet(sets[index], repeat, execution.Runs);

        return new SqlHarnessMeasureSetReport(
            target,
            repeat,
            execution.Runs.Count,
            execution.SetupExecutionCount,
            execution.WarmupOrder,
            MeasuredOrderRule,
            PlanCacheWarning,
            setReports,
            Summarize(setReports),
            ArtifactDirectory: null)
        {
            Warnings = execution.Warnings,
        };
    }

    private static MeasureParameterSetReport ProjectSet(
        PreparedMeasureParameterSet set,
        int repeat,
        IReadOnlyList<CollectedBenchmarkRun> measuredRuns)
    {
        // Runs are measured executions only. Warm-ups are not in this list.
        var runs = new List<CollectedBenchmarkRun>(measuredRuns.Count);
        foreach (var run in measuredRuns)
        {
            if (string.Equals(run.Artifact.ParameterSet, set.Name, StringComparison.Ordinal))
                runs.Add(run);
        }

        if (runs.Count == 0)
            throw new InvalidOperationException($"Parameter set '{set.Name}' has no measured runs.");

        string? resultHash = runs[0].ResultHash;
        var stable = true;
        for (var index = 1; index < runs.Count; index++)
        {
            if (!string.Equals(resultHash, runs[index].ResultHash, StringComparison.Ordinal))
            {
                stable = false;
                resultHash = null;
                break;
            }
        }

        return new MeasureParameterSetReport(
            set.Name,
            set.Metadata,
            set.ValueHash,
            repeat,
            stable,
            stable ? resultHash : null,
            BenchmarkReports.CreateVariantReport(set.Name, runs),
            DistinctPlanHashes(runs));
    }

    private static string[] DistinctPlanHashes(IReadOnlyList<CollectedBenchmarkRun> runs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new List<string>();
        foreach (var run in runs)
        {
            foreach (var hash in run.PlanHashes)
            {
                if (seen.Add(hash))
                    hashes.Add(hash);
            }
        }

        return hashes.ToArray();
    }

    private static MeasureCrossSetSummary Summarize(IReadOnlyList<MeasureParameterSetReport> sets)
    {
        // Placeholder zeros are not a ranking. Unavailable CPU, elapsed, or reads name no winner.
        var elapsedUnavailable = sets.Any(set => set.Metrics.MetricReport?.ElapsedTimeAvailability == BenchmarkMetricReport.Unavailable);
        var cpuUnavailable = sets.Any(set => set.Metrics.MetricReport?.CpuTimeAvailability == BenchmarkMetricReport.Unavailable);
        var readsUnavailable = sets.Any(set => set.Metrics.MetricReport?.LogicalReadsAvailability == BenchmarkMetricReport.Unavailable);
        var elapsed = elapsedUnavailable
            ? new MedianExtrema(null, 0, null, 0)
            : ExtremaElapsed(sets);
        var cpu = cpuUnavailable
            ? new MedianExtrema(null, 0, null, 0)
            : Extrema(sets, set => set.Metrics.CpuTimeMilliseconds.Median);
        var reads = readsUnavailable
            ? new MedianExtrema(null, 0, null, 0)
            : Extrema(sets, set => set.Metrics.LogicalReads.Median);
        var wholeMilliseconds = !elapsedUnavailable
            && sets.All(set => set.Metrics.MetricReport is not { ElapsedWholeMillisecondsAreExact: false });
        return new MeasureCrossSetSummary(
            elapsed.MinimumSet,
            elapsed.Minimum,
            elapsed.MaximumSet,
            elapsed.Maximum,
            cpu.MinimumSet,
            cpu.Minimum,
            cpu.MaximumSet,
            cpu.Maximum,
            reads.MinimumSet,
            reads.Minimum,
            reads.MaximumSet,
            reads.Maximum)
        {
            CpuTimeAvailability = cpuUnavailable ? BenchmarkMetricReport.Unavailable : BenchmarkMetricReport.Measured,
            ElapsedTimeAvailability = elapsedUnavailable ? BenchmarkMetricReport.Unavailable : BenchmarkMetricReport.Measured,
            LogicalReadsAvailability = readsUnavailable ? BenchmarkMetricReport.Unavailable : BenchmarkMetricReport.Measured,
            ElapsedWholeMillisecondsAreExact = wholeMilliseconds,
            MinimumMedianElapsedMillisecondsExact = elapsed.MinimumExact,
            MaximumMedianElapsedMillisecondsExact = elapsed.MaximumExact,
        };
    }

    private readonly record struct MedianExtrema(
        string? MinimumSet,
        long Minimum,
        string? MaximumSet,
        long Maximum,
        decimal? MinimumExact = null,
        decimal? MaximumExact = null);

    private static MedianExtrema ExtremaElapsed(IReadOnlyList<MeasureParameterSetReport> sets)
    {
        if (sets.Any(set => set.Metrics.MetricReport?.ElapsedTimeMillisecondsExact is null))
            return Extrema(sets, set => set.Metrics.ElapsedTimeMilliseconds.Median);

        var minimumSet = sets[0];
        var maximumSet = sets[0];
        var minimum = ExactMedian(minimumSet);
        var maximum = ExactMedian(maximumSet);
        for (var index = 1; index < sets.Count; index++)
        {
            var value = ExactMedian(sets[index]);
            if (value < minimum)
            {
                minimumSet = sets[index];
                minimum = value;
            }

            if (value > maximum)
            {
                maximumSet = sets[index];
                maximum = value;
            }
        }

        return new MedianExtrema(
            minimumSet.Name,
            minimumSet.Metrics.ElapsedTimeMilliseconds.Median,
            maximumSet.Name,
            maximumSet.Metrics.ElapsedTimeMilliseconds.Median,
            minimum,
            maximum);
    }

    private static decimal ExactMedian(MeasureParameterSetReport set) =>
        set.Metrics.MetricReport!.ElapsedTimeMillisecondsExact!.Median;

    private static MedianExtrema Extrema(
        IReadOnlyList<MeasureParameterSetReport> sets,
        Func<MeasureParameterSetReport, long> median)
    {
        var minimumSet = sets[0];
        var maximumSet = sets[0];
        var minimum = median(minimumSet);
        var maximum = median(maximumSet);
        for (var index = 1; index < sets.Count; index++)
        {
            var value = median(sets[index]);
            if (value < minimum)
            {
                minimumSet = sets[index];
                minimum = value;
            }

            if (value > maximum)
            {
                maximumSet = sets[index];
                maximum = value;
            }
        }

        return new MedianExtrema(minimumSet.Name, minimum, maximumSet.Name, maximum);
    }
}
