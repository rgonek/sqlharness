using System.Globalization;
using System.Text;
using System.Text.Json;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class Renderer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);
    public void Render(SqlHarnessOutcome outcome, OutputMode mode, OutputCaptureWriter output, string command = "unknown")
    {
        if (mode == OutputMode.Ndjson)
        {
            // The NDJSON watch stream owns its output; a stray report is never
            // re-rendered. Only a transport-level message may remain.
            if (!string.IsNullOrWhiteSpace(outcome.SafeError))
                output.WriteLine(SecretRedactor.Redact(outcome.SafeError, []));
            return;
        }
        if (mode == OutputMode.Agent)
        {
            WriteAgent(outcome, command, output, new AgentOutputOptions());
            return;
        }
        var report = outcome.Report is SqlHarnessArtifactReadReport artifactRead ? artifactRead.Section : outcome.Report;
        var hasError = outcome.Error is not null || !string.IsNullOrWhiteSpace(outcome.SafeError);
        if (hasError && mode is OutputMode.Json or OutputMode.JsonSummary)
        {
            var error = outcome.MachineError!;
            var payload = new { result = mode == OutputMode.JsonSummary ? ProjectSummary(report) : report, error };
            output.WriteLine(JsonSerializer.Serialize(payload, payload.GetType(), Json));
            return;
        }
        if (report is not null && mode is OutputMode.Json or OutputMode.JsonSummary)
        {
            var rendered = mode == OutputMode.JsonSummary ? ProjectSummary(report) : report;
            output.WriteLine(JsonSerializer.Serialize(rendered, rendered!.GetType(), Json));
            return;
        }
        if (report is SqlHarnessQueryReport query)
        {
            output.WriteLine($"Target: {query.Target.ActualServer}/{query.Target.ActualDatabase} ({query.Target.Mode})");
            output.WriteLine($"Statement: {query.StatementClassification}; duration: {query.DurationMilliseconds} ms");
            foreach (var set in query.ResultSets)
            {
                output.WriteLine(string.Join("\t", set.Columns.Select(c => c.Name)));
                foreach (var row in set.Rows) output.WriteLine(string.Join("\t", row.Select(Value)));
                if (set.OmittedRowCount > 0) output.WriteLine($"Omitted rows: {set.OmittedRowCount}");
            }
            foreach (var message in query.Messages) output.WriteLine(message);
        }
        else if (report is SqlHarnessDryRunReport dryRun)
            output.WriteLine($"Dry run {dryRun.Command}: allowed; classification: {dryRun.Classification}; tier: {dryRun.Tier}; parameters: {dryRun.Parameters.Count}; parameter sets: {dryRun.ParameterSets}; no connection opened.");
        else if (report is SqlHarnessMeasureReport measure)
            output.WriteLine($"measure\t{ElapsedText(measure.Query)}\nStable results: {measure.ResultsStable}; artifacts: {measure.ArtifactDirectory ?? "none"}");
        else if (report is SqlHarnessMeasureSetReport measureSet)
            RenderMeasureSet(measureSet, output);
        else if (report is SqlHarnessCompareReport compare)
            output.WriteLine($"baseline\t{ElapsedText(compare.Baseline)}\ncandidate\t{ElapsedText(compare.Candidate)}\n{FormatTechnicalEquivalence(compare.Equivalence)}; artifacts: {compare.ArtifactDirectory ?? "none"}{FormatRegressionLine(compare.Regression)}");
        else if (report is SqlHarnessCompareMatrixReport matrix)
            RenderMatrix(matrix, output);
        else if (report is DistilledPlan plan)
            RenderPlan(plan, output);
        else if (report is SqlHarnessWatchReport watch)
            RenderWatch(watch, output);
        else if (report is SqlHarnessSnapshotReport snapshot)
            RenderSnapshot(snapshot, output);
        else if (mode == OutputMode.Text && report is SqlHarnessCapabilities capabilities)
            output.WriteLine($"SQLHarness {capabilities.Version} ({capabilities.BuildId}); contract {capabilities.ContractVersion}; commands: {string.Join(", ", capabilities.Commands.Select(c => c.Name))}");
        if (!string.IsNullOrWhiteSpace(outcome.SafeError))
            output.WriteLine($"SQLHarness {outcome.ExitCode}: {SecretRedactor.Redact(outcome.SafeError, [])}");
    }

    public void RenderError(SqlHarnessExitCode exitCode, string message, OutputMode mode, string command, OutputCaptureWriter output, SqlHarnessError? structuredError = null, AgentOutputOptions? agentOptions = null)
    {
        var error = structuredError ?? SqlHarnessError.From(exitCode, message, "validation");
        if (mode == OutputMode.Ndjson)
        {
            // Pre-run NDJSON failures are plain lines: no stream started.
            output.WriteLine(message);
            return;
        }
        if (mode == OutputMode.Agent)
        {
            WriteAgent(new SqlHarnessOutcome(exitCode, null, message, Error: error), command, output, agentOptions ?? new AgentOutputOptions());
            return;
        }
        if (mode is OutputMode.Json or OutputMode.JsonSummary)
        {
            var payload = new { result = (object?)null, error };
            output.WriteLine(JsonSerializer.Serialize(payload, payload.GetType(), Json));
            return;
        }
        output.WriteLine(message);
    }

    public void RenderAgent(SqlHarnessOutcome outcome, string command, OutputCaptureWriter output, AgentOutputOptions options) =>
        WriteAgent(outcome, command, output, options);

    private static void WriteAgent(SqlHarnessOutcome outcome, string command, TextWriter output, AgentOutputOptions options)
    {
        var requestedBytes = options.MaximumBytes is >= 4096 and <= 1048576 ? options.MaximumBytes : 16 * 1024;
        var requestedCellChars = options.MaximumCellCharacters is >= 0 and <= AgentOutputLimits.MaximumCellCharacters ? options.MaximumCellCharacters : AgentOutputLimits.DefaultCellCharacters;
        var error = outcome.MachineError;
        var errorOmitted = 0;
        string Clip(string value)
        {
            if (value.Length <= requestedCellChars) return value;
            errorOmitted++;
            var length = requestedCellChars;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value[..length];
        }
        if (error is not null)
            error = error with { Message = Clip(error.Message), Hint = error.Hint is null ? null : Clip(error.Hint), Location = error.Location is null ? null : error.Location with { Path = error.Location.Path is null ? null : Clip(error.Location.Path) } };
        var boundedCommand = Clip(command);
        var report = outcome.Report is SqlHarnessArtifactReadReport artifactRead ? artifactRead.Section : outcome.Report;
        var status = error is null
            ? "success"
            : report is SqlHarnessCompareMatrixReport
                ? "partial"
                : "error";
        foreach (var detailLimit in AgentOutputProjection.CandidateDetailLimits)
        {
            var result = AgentOutputProjection.Project(report, requestedCellChars, detailLimit, out var omitted, requestedBytes);
            omitted += errorOmitted;
            var truncated = omitted > 0;
            var envelope = new SqlHarnessAgentEnvelope(AgentEnvelopeSchema.Version, boundedCommand, status, (int)outcome.ExitCode, result, error,
                truncated ? new { omittedItems = omitted, detailLimit, maxCellChars = requestedCellChars } : null,
                outcome.OperationRef, outcome.ParentOperationRef);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, CompactJson);
            if (bytes.Length + 1 <= requestedBytes)
            {
                output.Write(Encoding.UTF8.GetString(bytes));
                output.WriteLine();
                return;
            }
        }

        var minimal = new SqlHarnessAgentEnvelope(AgentEnvelopeSchema.Version, boundedCommand, "error", (int)SqlHarnessExitCode.Safety, null,
            new SqlHarnessError("output_budget_too_small", "render", "The minimum agent response does not fit the configured byte budget."),
            new { omittedItems = 1, maxOutputBytes = requestedBytes });
        var minimalBytes = JsonSerializer.SerializeToUtf8Bytes(minimal, CompactJson);
        if (minimalBytes.Length + 1 <= requestedBytes)
        {
            output.Write(Encoding.UTF8.GetString(minimalBytes));
            output.WriteLine();
        }
        else
        {
            // Minimum accepted budget is 4096 bytes, so this is defensive only.
            output.WriteLine($"{{\"schemaVersion\":{AgentEnvelopeSchema.Version},\"command\":\"unknown\",\"status\":\"error\",\"exitCode\":2,\"result\":null,\"error\":{{\"code\":\"output_budget_too_small\",\"phase\":\"render\",\"message\":\"Agent output unavailable.\"}},\"truncation\":null}}");
        }
    }

    private static object? ProjectSummary(object? report) => report switch
    {
        SqlHarnessCompareReport compare => BenchmarkSummaryProjector.Project(compare),
        SqlHarnessCompareMatrixReport matrix => BenchmarkSummaryProjector.Project(matrix),
        SqlHarnessMeasureReport measure => BenchmarkSummaryProjector.Project(measure),
        SqlHarnessMeasureSetReport measureSet => BenchmarkSummaryProjector.Project(measureSet),
        _ => report,
    };

    private static void RenderMeasureSet(SqlHarnessMeasureSetReport report, TextWriter output)
    {
        output.WriteLine(report.PlanCacheWarning);
        var warmup = report.WarmupOrder.Count == 0 ? "none" : string.Join(", ", report.WarmupOrder);
        output.WriteLine("Warm-up: " + warmup);
        output.WriteLine(report.MeasuredOrderRule);
        output.WriteLine("Setup executions: " + report.SetupExecutionCount.ToString(CultureInfo.InvariantCulture));
        foreach (var set in report.Sets)
        {
            var plans = set.PlanHashes.Count == 0 ? "none" : string.Join(',', set.PlanHashes);
            output.WriteLine(string.Join('\t',
                set.Name,
                "stable=" + (set.ResultsStable ? "true" : "false"),
                "elapsed=" + ElapsedText(set.Metrics),
                "cpu=" + CpuText(set.Metrics),
                "reads=" + ReadsText(set.Metrics),
                "plans=" + plans));
        }

        WriteCrossSet(report.CrossSetSummary, output);
        output.WriteLine("artifacts: " + (report.ArtifactDirectory ?? "none"));
    }

    private static void WriteCrossSet(MeasureCrossSetSummary cross, TextWriter output)
    {
        var elapsedMinimum = cross.MinimumMedianElapsedSet;
        var elapsedMaximum = cross.MaximumMedianElapsedSet;
        if (cross.ElapsedTimeAvailability == BenchmarkMetricReport.Unavailable || elapsedMinimum is null || elapsedMaximum is null)
            output.WriteLine("Cross-set elapsed: unavailable");
        else if (!cross.ElapsedWholeMillisecondsAreExact
            && cross.MinimumMedianElapsedMillisecondsExact is decimal minimumExact
            && cross.MaximumMedianElapsedMillisecondsExact is decimal maximumExact)
            output.WriteLine($"Cross-set elapsed: {elapsedMinimum} {FormatDecimal(minimumExact)} .. {elapsedMaximum} {FormatDecimal(maximumExact)}");
        else
            output.WriteLine(CrossSetLine("elapsed", elapsedMinimum, cross.MinimumMedianElapsedMilliseconds, elapsedMaximum, cross.MaximumMedianElapsedMilliseconds));

        var cpuMinimum = cross.MinimumMedianCpuSet;
        var cpuMaximum = cross.MaximumMedianCpuSet;
        if (cross.CpuTimeAvailability == BenchmarkMetricReport.Unavailable || cpuMinimum is null || cpuMaximum is null)
            output.WriteLine("Cross-set cpu: unavailable");
        else
            output.WriteLine(CrossSetLine("cpu", cpuMinimum, cross.MinimumMedianCpuMilliseconds, cpuMaximum, cross.MaximumMedianCpuMilliseconds));

        var readsMinimum = cross.MinimumMedianReadsSet;
        var readsMaximum = cross.MaximumMedianReadsSet;
        if (cross.LogicalReadsAvailability == BenchmarkMetricReport.Unavailable || readsMinimum is null || readsMaximum is null)
            output.WriteLine("Cross-set reads: unavailable");
        else
            output.WriteLine(CrossSetLine("reads", readsMinimum, cross.MinimumMedianLogicalReads, readsMaximum, cross.MaximumMedianLogicalReads));
    }

    private static string CrossSetLine(string metric, string minimumSet, long minimum, string maximumSet, long maximum) =>
        $"Cross-set {metric}: {minimumSet} {minimum.ToString(CultureInfo.InvariantCulture)} .. {maximumSet} {maximum.ToString(CultureInfo.InvariantCulture)}";

    private static void RenderMatrix(SqlHarnessCompareMatrixReport matrix, TextWriter output)
    {
        foreach (var cell in matrix.Cells)
        {
            output.WriteLine(string.Join('\t',
                cell.ParameterValue,
                FormatTechnicalEquivalence(cell.Compare.Equivalence),
                MedianElapsed(cell.Compare.Baseline),
                MedianElapsed(cell.Compare.Candidate),
                cell.Compare.ArtifactDirectory ?? "none"));
        }
    }

    private static void RenderWatch(SqlHarnessWatchReport watch, TextWriter output)
    {
        foreach (var poll in watch.EmittedPolls)
        {
            output.WriteLine(
                $"Poll {poll.Poll.ToString(CultureInfo.InvariantCulture)}; elapsed: {poll.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
            foreach (var set in poll.ResultSets)
            {
                output.WriteLine(string.Join("\t", set.Columns.Select(c => c.Name)));
                foreach (var row in set.Rows) output.WriteLine(string.Join("\t", row.Select(Value)));
                if (set.OmittedRowCount > 0) output.WriteLine($"Omitted rows: {set.OmittedRowCount}");
            }
        }

        output.WriteLine(
            $"Polls: {watch.PollCount.ToString(CultureInfo.InvariantCulture)}; elapsed: {watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms; exit reason: {FormatWatchExitReason(watch.ExitReason)}; changed polls: {watch.TotalChangedPolls.ToString(CultureInfo.InvariantCulture)}; omitted polls: {watch.OmittedPolls.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void RenderSnapshot(SqlHarnessSnapshotReport snapshot, TextWriter output)
    {
        var verdict = snapshot.Verdict switch
        {
            SnapshotVerdict.Stored => "stored",
            SnapshotVerdict.Identical => "identical",
            SnapshotVerdict.Different => $"{snapshot.DifferenceCount.ToString(CultureInfo.InvariantCulture)} differences",
            _ => snapshot.Verdict.ToString().ToLowerInvariant(),
        };
        output.WriteLine($"Snapshot {snapshot.Name}: {verdict}");
        foreach (var difference in snapshot.Differences)
        {
            output.WriteLine(string.Join('\t',
                difference.ResultSet.ToString(CultureInfo.InvariantCulture),
                difference.Row?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                difference.Column?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                difference.Kind));
        }
    }

    private static string FormatWatchExitReason(WatchExitReason reason) => reason switch
    {
        WatchExitReason.ConditionMet => "condition-met",
        WatchExitReason.Unchanged => "unchanged",
        WatchExitReason.MaxDuration => "max-duration",
        _ => reason.ToString().ToLowerInvariant(),
    };

    private static string Value(object? value) => value is null ? "NULL" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Dist(CompareDistribution d) => $"{d.Min}/{d.Median}/{d.Max}";

    private static string ElapsedText(CompareVariantReport variant)
    {
        var metric = variant.MetricReport;
        if (metric is null)
            return Dist(variant.ElapsedTimeMilliseconds);
        if (metric.ElapsedTimeAvailability == BenchmarkMetricReport.Unavailable || metric.ElapsedTimeMillisecondsExact is null)
            return "unavailable";
        if (!metric.ElapsedWholeMillisecondsAreExact)
            return FormatFractional(metric.ElapsedTimeMillisecondsExact);
        return Dist(variant.ElapsedTimeMilliseconds);
    }

    private static string MedianElapsed(CompareVariantReport variant)
    {
        var metric = variant.MetricReport;
        if (metric is { ElapsedTimeAvailability: BenchmarkMetricReport.Unavailable })
            return "unavailable";
        if (metric is { ElapsedWholeMillisecondsAreExact: false, ElapsedTimeMillisecondsExact: { } exact })
            return FormatDecimal(exact.Median);
        return variant.ElapsedTimeMilliseconds.Median.ToString(CultureInfo.InvariantCulture);
    }

    private static string CpuText(CompareVariantReport variant) =>
        variant.MetricReport?.CpuTimeAvailability == BenchmarkMetricReport.Unavailable
            ? "unavailable"
            : Dist(variant.CpuTimeMilliseconds);

    private static string ReadsText(CompareVariantReport variant) =>
        variant.MetricReport?.LogicalReadsAvailability == BenchmarkMetricReport.Unavailable
            ? "unavailable"
            : Dist(variant.LogicalReads);

    private static string FormatFractional(FractionalMilliseconds value) =>
        $"{FormatDecimal(value.Min)}/{FormatDecimal(value.Median)}/{FormatDecimal(value.Max)}";

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    // Leading newline so a missing verdict (legacy artifact) adds no line.
    private static string FormatRegressionLine(RegressionReport? regression)
    {
        if (regression is null)
            return string.Empty;

        if (regression.Verdict == RegressionPolicy.Inconclusive)
            return $"\nRegression: inconclusive ({string.Join(", ", regression.Reasons)})";

        var elapsed = regression.Elapsed;
        if (elapsed.Delta is not { } delta)
            return $"\nRegression: {regression.Verdict}";

        var text = $"elapsed {FormatSigned(delta)} ms";
        if (elapsed.DeltaPercent is { } percent)
            text += $", {FormatSigned(percent)} %";
        return $"\nRegression: {regression.Verdict} ({text})";
    }

    private static string FormatSigned(decimal value) =>
        (value < 0 ? "-" : "+") + FormatDecimal(Math.Abs(value));

    private static string FormatTechnicalEquivalence(ResultEquivalenceReport equivalence)
    {
        if (equivalence.Mode == ResultComparisonMode.Off)
            return "Technical equivalence: off";

        var mode = equivalence.Mode switch
        {
            ResultComparisonMode.Ordered => "ordered",
            ResultComparisonMode.Multiset => "multiset",
            ResultComparisonMode.Set => "set",
            _ => equivalence.Mode.ToString().ToLowerInvariant(),
        };
        var label = equivalence.CompareNullability ? $"{mode}, strict nullability" : mode;
        var text =
            $"Technical equivalence ({label}): {equivalence.Equivalent}; baseline-only: {equivalence.BaselineOnlyCount}; candidate-only: {equivalence.CandidateOnlyCount}";
        // Differing positions is ordered-only; multiset/set leave it null.
        if (equivalence.DifferingPositions is not null)
            text += $"; differing positions: {equivalence.DifferingPositions}";
        text += $"; nullability: {(equivalence.NullabilityEquivalent is { } nullability ? nullability.ToString() : "unavailable")}";
        if (equivalence.MismatchReason is not null)
            text += $"; mismatch reason: {equivalence.MismatchReason}";
        return text;
    }
    private static void RenderPlan(DistilledPlan plan, TextWriter output)
    {
        foreach (var statement in plan.Statements)
        {
            RenderNode(statement.Root, 0, output);
            foreach (var index in statement.MissingIndexes)
            {
                var table = index.Schema is { Length: > 0 } schema ? $"{schema}.{index.Table}" : index.Table;
                output.WriteLine($"Missing index: {table} — impact {index.Impact.ToString("0.##", CultureInfo.InvariantCulture)} — equality [{string.Join(", ", index.EqualityColumns)}] — inequality [{string.Join(", ", index.InequalityColumns)}] — include [{string.Join(", ", index.IncludeColumns)}]");
            }
        }
    }
    private static void RenderNode(PlanNode node, int depth, TextWriter output)
    {
        var target = string.Join(' ', new[] { node.ObjectName, node.IndexName }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var warnings = node.Warnings.Count == 0 ? "none" : string.Join(", ", node.Warnings);
        output.WriteLine($"{new string(' ', depth * 2)}{node.PhysicalOp}{(target.Length == 0 ? string.Empty : " " + target)} — est {Number(node.EstimatedRows)} / act {node.ActualRows?.ToString(CultureInfo.InvariantCulture) ?? "?"} — {warnings}");
        foreach (var child in node.Children) RenderNode(child, depth + 1, output);
    }
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";
}