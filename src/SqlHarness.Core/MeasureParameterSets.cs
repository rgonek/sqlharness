using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.SqlServer.Types;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

public sealed record MeasureParameterMetadata(string Name, string Type);

internal sealed record PreparedMeasureParameterSet(
    string Name,
    IReadOnlyList<SqlHarnessParameter> Parameters,
    IReadOnlyList<MeasureParameterMetadata> Metadata,
    string ValueHash);

internal static class MeasureParameterSetValidator
{
    internal static IReadOnlyList<PreparedMeasureParameterSet> Prepare(
        IReadOnlyList<string> fixedParameters,
        IReadOnlyList<SqlHarnessParameterSetInput> parameterSets,
        string? setupSql,
        string querySql,
        ISqlDialect? dialect = null)
    {
        ArgumentNullException.ThrowIfNull(fixedParameters);
        ArgumentNullException.ThrowIfNull(parameterSets);
        ArgumentNullException.ThrowIfNull(querySql);
        dialect ??= SqlDialects.For(SqlEngine.SqlServer);

        var fixedParsed = ParseShaped(fixedParameters, setName: null);
        var fixedNames = new HashSet<string>(
            fixedParsed.Select(parameter => parameter.Name),
            StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared = new List<PreparedMeasureParameterSet>(parameterSets.Count);
        IReadOnlyList<ShapedParameter>? baseline = null;
        string? baselineName = null;

        foreach (var set in parameterSets)
        {
            ArgumentNullException.ThrowIfNull(set);
            ArgumentNullException.ThrowIfNull(set.Name);
            ArgumentNullException.ThrowIfNull(set.Parameters);
            if (!seenNames.Add(set.Name))
                throw new SqlHarnessSafetyException($"Duplicate parameter set '{set.Name}'.");

            var parsed = ParseShaped(set.Parameters, set.Name);
            foreach (var parameter in parsed)
            {
                if (fixedNames.Contains(parameter.Name))
                {
                    throw new SqlHarnessSafetyException(
                        $"Parameter set '{set.Name}' repeats fixed SQL parameter '{parameter.Name}'.");
                }
            }

            var ordered = Order(fixedParsed.Concat(parsed));
            if (baseline is null || baselineName is null)
            {
                baseline = ordered;
                baselineName = set.Name;
            }
            else
            {
                EnsureSameShape(baselineName, baseline, set.Name, ordered);
            }

            var parameters = ordered.Select(parameter => parameter.Parameter).ToArray();
            dialect.ValidateParameterReferences(parameters, setupSql, querySql);
            prepared.Add(new PreparedMeasureParameterSet(
                set.Name,
                parameters,
                ToMetadata(parameters),
                TypedParameterHasher.Hash(parameters)));
        }

        return prepared;
    }

    private static IReadOnlyList<SqlHarnessParameter> Parse(IReadOnlyList<string> inputs, string? setName)
    {
        try
        {
            return SqlParameterParser.Parse(inputs);
        }
        catch (SqlHarnessSafetyException exception) when (exception.IsParameterValue)
        {
            // Keep name and type on the public exception. Do not reattach Diagnostic as InnerException.
            var message = setName is null
                ? exception.Message
                : $"Parameter set '{setName}' is invalid: {exception.Message}";
            throw exception.WithParameterValue(message);
        }
        catch (SqlHarnessSafetyException exception)
        {
            var message = setName is null
                ? exception.Message
                : $"Parameter set '{setName}' is invalid: {exception.Message}";
            throw new SqlHarnessSafetyException(message);
        }
    }

    private static IReadOnlyList<ShapedParameter> ParseShaped(IReadOnlyList<string> inputs, string? setName)
    {
        var parsed = Parse(inputs, setName);
        var shaped = new ShapedParameter[parsed.Count];
        for (var i = 0; i < parsed.Count; i++)
            shaped[i] = new ShapedParameter(parsed[i], DeclaredShape.From(TypeToken(inputs[i])));

        return shaped;
    }

    // The text between ':' and '=' (or the type in name:type:null). Untyped values are nvarchar.
    private static string? TypeToken(string input)
    {
        var equals = input.IndexOf('=');
        if (equals >= 0)
        {
            var declaration = input[..equals];
            var colon = declaration.IndexOf(':');
            return colon < 0 ? null : declaration[(colon + 1)..];
        }

        var lastColon = input.LastIndexOf(':');
        if (lastColon <= 0)
            return null;

        var left = input[..lastColon];
        var typeColon = left.IndexOf(':');
        return typeColon < 0 ? null : left[(typeColon + 1)..];
    }

    private static ShapedParameter[] Order(IEnumerable<ShapedParameter> parameters) =>
        parameters
            .OrderBy(parameter => parameter.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToArray();

    private static MeasureParameterMetadata[] ToMetadata(IReadOnlyList<SqlHarnessParameter> parameters) =>
        parameters
            .Select(parameter => new MeasureParameterMetadata(parameter.Name, MeasureParameterTyping.TypeName(parameter)))
            .ToArray();

    private static void EnsureSameShape(
        string baselineName,
        IReadOnlyList<ShapedParameter> baseline,
        string candidateName,
        IReadOnlyList<ShapedParameter> candidate)
    {
        var baselineByName = Index(baseline);
        var candidateByName = Index(candidate);

        foreach (var parameter in baseline)
        {
            if (!candidateByName.ContainsKey(parameter.Name))
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' is missing SQL parameter '{parameter.Name}'.");
            }
        }

        foreach (var parameter in candidate)
        {
            if (!baselineByName.ContainsKey(parameter.Name))
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' has unexpected SQL parameter '{parameter.Name}'.");
            }
        }

        foreach (var parameter in baseline)
        {
            var other = candidateByName[parameter.Name];
            var name = parameter.Name;
            var left = parameter.Shape;
            var right = other.Shape;
            if (!string.Equals(left.Type, right.Type, StringComparison.Ordinal))
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' SQL parameter '{name}' has a different type than parameter set '{baselineName}'.");
            }

            if (left.Size != right.Size)
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' SQL parameter '{name}' has a different size than parameter set '{baselineName}'.");
            }

            if (left.Precision != right.Precision)
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' SQL parameter '{name}' has a different precision than parameter set '{baselineName}'.");
            }

            if (left.Scale != right.Scale)
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' SQL parameter '{name}' has a different scale than parameter set '{baselineName}'.");
            }
        }
    }

    private static Dictionary<string, ShapedParameter> Index(IReadOnlyList<ShapedParameter> parameters) =>
        parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);

    private sealed record ShapedParameter(SqlHarnessParameter Parameter, DeclaredShape Shape)
    {
        public string Name => Parameter.Name;
    }

    // Declared type only. Size is null when the token has no length, and -1 for (max).
    // numeric is the decimal alias. Bind size on SqlHarnessParameter is not part of this shape.
    private readonly record struct DeclaredShape(string Type, int? Size, byte? Precision, byte? Scale)
    {
        public static DeclaredShape From(string? token)
        {
            if (string.IsNullOrEmpty(token))
                return new("nvarchar", null, null, null);

            token = token.ToLowerInvariant();
            var open = token.IndexOf('(');
            if (open < 0)
                return new(token is "numeric" ? "decimal" : token, null, null, null);

            if (!token.EndsWith(')') || open == 0)
                throw new SqlHarnessSafetyException("SQL parameter shape is invalid.");

            var head = token[..open];
            var body = token[(open + 1)..^1];
            if (head is "decimal" or "numeric")
            {
                var comma = body.IndexOf(',');
                if (comma <= 0
                    || !byte.TryParse(body[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out var precision)
                    || !byte.TryParse(body[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var scale))
                {
                    throw new SqlHarnessSafetyException("SQL parameter shape is invalid.");
                }

                return new("decimal", null, precision, scale);
            }

            if (body == "max")
                return new(head, -1, null, null);

            if (!int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw new SqlHarnessSafetyException("SQL parameter shape is invalid.");

            return new(head, size, null, null);
        }
    }
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
    IReadOnlyList<CollectedBenchmarkRun> Runs);

internal sealed class MeasureParameterSetRunner
{
    private readonly ISqlDialect _dialect;
    private readonly int _comparisonMaximumRows;

    internal MeasureParameterSetRunner(ISqlDialect dialect, int comparisonMaximumRows)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        ArgumentOutOfRangeException.ThrowIfNegative(comparisonMaximumRows);
        _comparisonMaximumRows = comparisonMaximumRows;
    }

    internal async Task<MeasureParameterSetExecution> ExecuteAsync(
        ISqlSession session,
        SqlHarnessMeasureOperation operation,
        IReadOnlyList<PreparedMeasureParameterSet> sets,
        CanonicalResultAccumulator raw,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(raw);

        var setupExecutionCount = 0;
        if (!string.IsNullOrWhiteSpace(operation.SetupSql))
        {
            await BenchmarkRunner.ExecuteRawAsync(
                session,
                new SqlExecutionCommand(operation.SetupSql, sets[0].Parameters, operation.TimeoutSeconds),
                raw,
                ct);
            setupExecutionCount = 1;
        }

        var warmupOrder = new List<string>(sets.Count);
        foreach (var set in sets)
        {
            await ExecuteSetAsync(set, repetition: 0);
            warmupOrder.Add(set.Name);
        }

        var runs = new List<CollectedBenchmarkRun>(operation.Repeat * sets.Count);
        for (var round = 1; round <= operation.Repeat; round++)
        {
            var start = round % sets.Count;
            for (var offset = 0; offset < sets.Count; offset++)
            {
                var set = sets[(start + offset) % sets.Count];
                runs.Add(await ExecuteSetAsync(set, repetition: round));
            }
        }

        return new MeasureParameterSetExecution(setupExecutionCount, warmupOrder, runs);

        Task<CollectedBenchmarkRun> ExecuteSetAsync(PreparedMeasureParameterSet set, int repetition) =>
            BenchmarkRunner.ExecuteAsync(
                _dialect,
                session,
                operation.QuerySql,
                set.Parameters,
                operation.TimeoutSeconds,
                repetition,
                "measure",
                set.Name,
                raw,
                captureComparison: false,
                _comparisonMaximumRows,
                ct);
    }
}

internal static class MeasureParameterSetReportProjector
{
    internal const string MeasuredOrderRule =
        "In one-based round r, measured execution starts at index r modulo setCount and wraps in user-supplied order.";

    internal const string PlanCacheWarning =
        "Parameter-set measurements use the observed server plan-cache state; SQLHarness did not clear or isolate the plan cache.";

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

        var setReports = new MeasureParameterSetReport[sets.Count];
        for (var index = 0; index < sets.Count; index++)
            setReports[index] = ProjectSet(sets[index], repeat, execution.Runs);

        return new SqlHarnessMeasureSetReport(
            target,
            repeat,
            repeat * sets.Count,
            execution.SetupExecutionCount,
            execution.WarmupOrder,
            MeasuredOrderRule,
            PlanCacheWarning,
            setReports,
            Summarize(setReports),
            ArtifactDirectory: null);
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