using System.Globalization;
using System.Text.Json;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli.Commands;

public sealed class Renderer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public void Render(SqlHarnessOutcome outcome, bool json, OutputCaptureWriter output)
    {
        if (outcome.Report is not null && json)
        {
            output.WriteLine(JsonSerializer.Serialize(outcome.Report, outcome.Report.GetType(), Json));
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
            output.WriteLine($"baseline\t{Dist(compare.Baseline.ElapsedTimeMilliseconds)}\ncandidate\t{Dist(compare.Candidate.ElapsedTimeMilliseconds)}\nEquivalent results: {compare.ResultsEquivalent}; artifacts: {compare.ArtifactDirectory ?? "none"}");
        else if (outcome.Report is SqlHarnessGainReport gain)
        {
            output.WriteLine("Scope\tExecutions\tFailures\tSaved tokens\tSavings %");
            WriteGain("total", gain.Total, output); WriteGain("query", gain.Query, output);
            WriteGain("compare", gain.Compare, output); WriteGain("measure", gain.Measure, output);
        }
        else if (outcome.Report is DistilledPlan plan)
            RenderPlan(plan, output);
        else if (!string.IsNullOrWhiteSpace(outcome.SafeError))
            output.WriteLine($"SQLHarness {outcome.ExitCode}: {SecretRedactor.Redact(outcome.SafeError, [])}");
    }
    private static string Value(object? value) => value is null ? "NULL" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Dist(CompareDistribution d) => $"{d.Min}/{d.Median}/{d.Max}";
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
