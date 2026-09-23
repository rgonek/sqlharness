using System.Globalization;
using System.Text.Json;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class Renderer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public void Render(SqlHarnessOutcome outcome, OutputMode mode, OutputCaptureWriter output)
    {
        if (outcome.Report is not null && mode is OutputMode.Json or OutputMode.JsonSummary)
        {
            var rendered = mode == OutputMode.JsonSummary
                ? outcome.Report switch
                {
                    SqlHarnessCompareReport compare => (object)BenchmarkSummaryProjector.Project(compare),
                    SqlHarnessCompareMatrixReport matrix => BenchmarkSummaryProjector.Project(matrix),
                    SqlHarnessMeasureReport measure => BenchmarkSummaryProjector.Project(measure),
                    _ => outcome.Report,
                }
                : outcome.Report;
            output.WriteLine(JsonSerializer.Serialize(rendered, rendered.GetType(), Json));
            return;
        }
        if (outcome.Report is SqlHarnessQueryReport query)
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
        else if (outcome.Report is SqlHarnessMeasureReport measure)
            output.WriteLine($"measure\t{Dist(measure.Query.ElapsedTimeMilliseconds)}\nStable results: {measure.ResultsStable}; artifacts: {measure.ArtifactDirectory ?? "none"}");
        else if (outcome.Report is SqlHarnessCompareReport compare)
            output.WriteLine($"baseline\t{Dist(compare.Baseline.ElapsedTimeMilliseconds)}\ncandidate\t{Dist(compare.Candidate.ElapsedTimeMilliseconds)}\n{FormatTechnicalEquivalence(compare.Equivalence)}; artifacts: {compare.ArtifactDirectory ?? "none"}");
        else if (outcome.Report is SqlHarnessCompareMatrixReport matrix)
            RenderMatrix(matrix, output);
        else if (outcome.Report is SqlHarnessGainReport gain)
        {
            output.WriteLine("Scope\tExecutions\tFailures\tSaved tokens\tSavings %");
            WriteGain("total", gain.Total, output); WriteGain("query", gain.Query, output);
            WriteGain("compare", gain.Compare, output); WriteGain("measure", gain.Measure, output);
            WriteGain("ping", gain.Ping, output); WriteGain("counts", gain.Counts, output);
            WriteGain("space", gain.Space, output);
            WriteGain("watch", gain.Watch, output); WriteGain("snapshot", gain.Snapshot, output);
            WriteGain("qstop", gain.QueryStoreTop, output);
        }
        else if (outcome.Report is DistilledPlan plan)
            RenderPlan(plan, output);
        else if (outcome.Report is SqlHarnessSchemaReport schema)
        {
            output.WriteLine($"Target: {schema.Target.ActualServer}/{schema.Target.ActualDatabase} ({schema.Target.Mode})");
            foreach (var obj in schema.Objects)
            {
                output.WriteLine($"{obj.Kind}\t{obj.Schema}.{obj.Name}");
                foreach (var c in obj.Columns) output.WriteLine($"c\t{c.Name}\t{c.Type}\t{(c.Nullable ? "null" : "not-null")}{(c.InPrimaryKey ? "\tpk" : "")}");
                foreach (var i in obj.Indexes) output.WriteLine($"i\t{i.Name}\t{(i.Unique ? "unique" : "nonunique")}\tkeys={string.Join(',', i.Keys)}\tinclude={string.Join(',', i.Includes)}{(i.Filter is null ? "" : $"\tfilter={i.Filter}")}");
                foreach (var f in obj.ForeignKeys) output.WriteLine($"fk\t{f.Name}\t{f.Columns}->{f.ReferencedTable}({f.ReferencedColumns})");
            }
            if (schema.OmittedObjects > 0) output.WriteLine($"Omitted objects: {schema.OmittedObjects}");
        }
        else if (outcome.Report is SqlHarnessPingReport ping)
            output.WriteLine($"Ready: {ping.Server}/{ping.Database} as {ping.Login}; {ping.DurationMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
        else if (outcome.Report is SqlHarnessCountsReport counts)
        {
            output.WriteLine("Schema\tName\tRows\tMethod");
            foreach (var table in counts.Tables)
                output.WriteLine($"{table.Schema}\t{table.Name}\t{table.Rows.ToString(CultureInfo.InvariantCulture)}\t{table.Method}");
            if (counts.Omitted > 0)
                output.WriteLine($"Omitted tables: {counts.Omitted.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (outcome.Report is SqlHarnessSpaceReport space)
            RenderSpace(space, output);
        else if (outcome.Report is SqlHarnessWatchReport watch)
            RenderWatch(watch, output);
        else if (outcome.Report is SqlHarnessSnapshotReport snapshot)
            RenderSnapshot(snapshot, output);
        else if (outcome.Report is SqlHarnessQueryStoreTopReport queryStoreTop)
            RenderQueryStoreTop(queryStoreTop, output);
        else if (!string.IsNullOrWhiteSpace(outcome.SafeError))
            output.WriteLine($"SQLHarness {outcome.ExitCode}: {SecretRedactor.Redact(outcome.SafeError, [])}");
    }

    private static void RenderMatrix(SqlHarnessCompareMatrixReport matrix, TextWriter output)
    {
        foreach (var cell in matrix.Cells)
        {
            output.WriteLine(string.Join('\t',
                cell.ParameterValue,
                FormatTechnicalEquivalence(cell.Compare.Equivalence),
                cell.Compare.Baseline.ElapsedTimeMilliseconds.Median.ToString(CultureInfo.InvariantCulture),
                cell.Compare.Candidate.ElapsedTimeMilliseconds.Median.ToString(CultureInfo.InvariantCulture),
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
            $"Polls: {watch.PollCount.ToString(CultureInfo.InvariantCulture)}; elapsed: {watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms; exit reason: {FormatWatchExitReason(watch.ExitReason)}");
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

    private static void RenderQueryStoreTop(SqlHarnessQueryStoreTopReport report, TextWriter output)
    {
        var minutes = report.WindowMinutes.ToString(CultureInfo.InvariantCulture);
        output.WriteLine($"Target: {report.Target.ActualServer}/{report.Target.ActualDatabase} ({report.Target.Mode})");
        output.WriteLine($"Window: {minutes} minutes");
        output.WriteLine($"Artifacts: {report.ArtifactDirectory ?? "none"}");
        if (report.Queries.Count == 0)
        {
            output.WriteLine($"No Query Store runtime data in the selected {minutes}-minute window.");
            return;
        }

        output.WriteLine(string.Join('\t',
            "QueryId", "Hash", "Object", "Executions", "Plans",
            "TotalDuration", "AverageDuration", "MaximumDuration",
            "TotalCpu", "AverageCpu", "MaximumCpu",
            "TotalLogicalReads", "AverageLogicalReads", "MaximumLogicalReads",
            "LastExecution"));
        foreach (var query in report.Queries)
        {
            output.WriteLine(string.Join('\t',
                query.QueryId.ToString(CultureInfo.InvariantCulture),
                query.QueryHash,
                query.ObjectName ?? string.Empty,
                query.ExecutionCount.ToString(CultureInfo.InvariantCulture),
                query.PlanCount.ToString(CultureInfo.InvariantCulture),
                DecimalText(query.TotalDurationMilliseconds),
                DecimalText(query.AverageDurationMilliseconds),
                DecimalText(query.MaximumDurationMilliseconds),
                DecimalText(query.TotalCpuMilliseconds),
                DecimalText(query.AverageCpuMilliseconds),
                DecimalText(query.MaximumCpuMilliseconds),
                DecimalText(query.TotalLogicalReads),
                DecimalText(query.AverageLogicalReads),
                DecimalText(query.MaximumLogicalReads),
                query.LastExecutionAt.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    private static void RenderSpace(SqlHarnessSpaceReport space, TextWriter output)
    {
        output.WriteLine("Files");
        foreach (var file in space.Files)
        {
            output.WriteLine(string.Join('\t',
                file.LogicalName,
                file.Type,
                file.PhysicalName ?? string.Empty,
                Mb(file.SizeMb),
                Mb(file.UsedMb),
                Mb(file.FreeMb)));
        }

        output.WriteLine("Allocation");
        output.WriteLine(string.Join('\t',
            Mb(space.Allocation.ReservedMb),
            Mb(space.Allocation.UsedMb),
            Mb(space.Allocation.DataMb)));

        output.WriteLine("Tables");
        foreach (var table in space.Tables)
        {
            output.WriteLine(string.Join('\t',
                table.Schema,
                table.Name,
                table.Rows.ToString(CultureInfo.InvariantCulture),
                Mb(table.ReservedMb),
                Mb(table.UsedMb),
                Mb(table.DataMb)));
        }

        if (space.Indexes.Count == 0)
            return;

        output.WriteLine("Indexes");
        foreach (var index in space.Indexes)
        {
            output.WriteLine(string.Join('\t',
                index.Schema,
                index.Table,
                index.Index,
                index.Type,
                Mb(index.ReservedMb),
                Mb(index.UsedMb),
                Mb(index.DataMb),
                index.Compression ?? string.Empty));
        }
    }

    private static string Mb(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string DecimalText(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Value(object? value) => value is null ? "NULL" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Dist(CompareDistribution d) => $"{d.Min}/{d.Median}/{d.Max}";
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
        var text =
            $"Technical equivalence ({mode}): {equivalence.Equivalent}; baseline-only: {equivalence.BaselineOnlyCount}; candidate-only: {equivalence.CandidateOnlyCount}";
        // Differing positions is ordered-only; multiset/set leave it null.
        if (equivalence.DifferingPositions is not null)
            text += $"; differing positions: {equivalence.DifferingPositions}";
        return text;
    }
    private static void WriteGain(string name, SqlHarnessGainSummary s, TextWriter output) => output.WriteLine($"{name}\t{s.Executions}\t{s.Failures}\t{s.SavedEstimatedTokens}\t{s.SavingsPercentage:0.##}");
    private static void RenderPlan(DistilledPlan plan, TextWriter output)
    {
        foreach (var statement in plan.Statements)
        {
            RenderNode(statement.Root, 0, output);
            foreach (var index in statement.MissingIndexes)
                output.WriteLine($"Missing index: {index.Table} — impact {index.Impact.ToString("0.##", CultureInfo.InvariantCulture)} — equality [{string.Join(", ", index.EqualityColumns)}] — inequality [{string.Join(", ", index.InequalityColumns)}] — include [{string.Join(", ", index.IncludeColumns)}]");
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