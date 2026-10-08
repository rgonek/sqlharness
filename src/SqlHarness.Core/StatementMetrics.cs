using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SqlHarness.Core;

public sealed record StatementOperatorSummary(
    int NodeId,
    string PhysicalOp,
    string? Object,
    string? Index,
    double? EstimatedRows,
    long? ActualRows,
    long? Executions);

public sealed record StatementMetrics(
    int StatementOrdinal,
    string StatementHash,
    long? CpuTimeMilliseconds,
    long? ElapsedTimeMilliseconds,
    int? DegreeOfParallelism,
    IReadOnlyList<StatementOperatorSummary> TopOperators);

/// <summary>Extracts bounded per-statement diagnostics from an actual SQL Server showplan.</summary>
internal static class StatementMetricsExtractor
{
    internal const int MaximumStatements = 128;
    internal const int MaximumOperators = 10;
    internal const int MaximumTextCharacters = 512;

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    internal static IReadOnlyList<StatementMetrics> Extract(string document, out int omittedStatements)
    {
        omittedStatements = 0;
        if (string.IsNullOrWhiteSpace(document) || document.AsSpan().TrimStart()[0] is '{' or '[')
            return [];

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024,
                MaxCharactersFromEntities = 0,
            };
            using var text = new StringReader(document);
            using var reader = XmlReader.Create(text, settings);
            var xml = XDocument.Load(reader, LoadOptions.None);
            if (xml.Root?.Name != Showplan + "ShowPlanXML")
                return [];

            var statements = xml.Descendants(Showplan + "StmtSimple").ToArray();
            omittedStatements = Math.Max(0, statements.Length - MaximumStatements);
            return statements.Take(MaximumStatements).Select((statement, ordinal) => Parse(statement, ordinal)).ToArray();
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or FormatException or OverflowException)
        {
            return [];
        }
    }

    private static StatementMetrics Parse(XElement statement, int ordinal)
    {
        var queryPlan = statement.Element(Showplan + "QueryPlan");
        var text = Attribute(statement, "StatementText") ?? string.Empty;
        var operators = queryPlan?.Descendants(Showplan + "RelOp")
            .Select(ParseOperator)
            .OrderByDescending(item => item.ActualRows.HasValue)
            .ThenByDescending(item => item.ActualRows)
            .ThenByDescending(item => item.Executions)
            .ThenBy(item => item.NodeId)
            .Take(MaximumOperators)
            .ToArray() ?? [];

        return new StatementMetrics(
            ordinal,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            Long(queryPlan?.Element(Showplan + "QueryTimeStats"), "CpuTime"),
            Long(queryPlan?.Element(Showplan + "QueryTimeStats"), "ElapsedTime"),
            Int(queryPlan, "DegreeOfParallelism"),
            operators);
    }

    private static StatementOperatorSummary ParseOperator(XElement relOp)
    {
        var own = relOp.DescendantsAndSelf().Where(element => element == relOp
            || !element.Ancestors(Showplan + "RelOp").SkipWhile(parent => parent != relOp).Skip(1).Any()).ToArray();
        var objectElement = own.FirstOrDefault(element => element.Name == Showplan + "Object");
        var counters = own.Where(element => element.Name == Showplan + "RunTimeCountersPerThread").ToArray();
        var actualRows = Sum(counters, "ActualRows");
        var executions = Sum(counters, "ActualExecutions");
        return new StatementOperatorSummary(
            Int(relOp, "NodeId") ?? -1,
            Clip(Attribute(relOp, "PhysicalOp") ?? string.Empty)!,
            Clip(Attribute(objectElement, "Table")),
            Clip(Attribute(objectElement, "Index")),
            Double(relOp, "EstimateRows"),
            actualRows,
            executions);
    }

    private static long? Sum(IReadOnlyList<XElement> elements, string name)
    {
        long total = 0;
        var found = false;
        foreach (var element in elements)
        {
            var value = Long(element, name);
            if (value is null)
                continue;
            found = true;
            total = total > long.MaxValue - value.Value ? long.MaxValue : total + value.Value;
        }

        return found ? total : null;
    }

    private static string? Attribute(XElement? element, string name) =>
        element?.Attribute(name)?.Value;

    private static string? Clip(string? value) => value is { Length: > MaximumTextCharacters }
        ? value[..MaximumTextCharacters]
        : value;

    private static long? Long(XElement? element, string name) =>
        long.TryParse(Attribute(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;

    private static int? Int(XElement? element, string name) =>
        int.TryParse(Attribute(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;

    private static double? Double(XElement? element, string name) =>
        double.TryParse(Attribute(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && value >= 0
            ? value
            : null;
}

internal static class ArtifactStatementProjector
{
    internal static ArtifactStatementsSection Project(string artifactKind, IReadOnlyList<CompareRunArtifact> runs)
    {
        var extracted = new List<(CompareRunArtifact Run, StatementMetrics Metric)>();
        var omittedFromPlans = 0;
        foreach (var run in runs)
        {
            var ordinalOffset = 0;
            foreach (var plan in run.PlanXmls)
            {
                var statements = StatementMetricsExtractor.Extract(plan, out var omitted);
                omittedFromPlans += omitted;
                extracted.AddRange(statements.Select(statement => (run, statement with
                {
                    StatementOrdinal = statement.StatementOrdinal + ordinalOffset,
                })));
                ordinalOffset += statements.Count + omitted;
            }
        }

        var aggregated = extracted
            .GroupBy(item => (item.Run.Variant, item.Run.ParameterSet, item.Run.MatrixCell, item.Metric.StatementOrdinal))
            .Select(group =>
            {
                var samples = group.ToArray();
                var first = samples[0].Metric;
                var dops = samples.Select(sample => sample.Metric.DegreeOfParallelism).OfType<int>().ToArray();
                return new ArtifactStatementMetric(
                    group.Key.Variant,
                    group.Key.ParameterSet,
                    group.Key.MatrixCell,
                    group.Key.StatementOrdinal,
                    first.StatementHash,
                    Median(samples.Select(sample => sample.Metric.CpuTimeMilliseconds)),
                    Median(samples.Select(sample => sample.Metric.ElapsedTimeMilliseconds)),
                    dops.Length == 0 ? null : dops.Max(),
                    first.TopOperators);
            })
            .OrderBy(statement => statement.Variant, StringComparer.Ordinal)
            .ThenBy(statement => statement.MatrixCell)
            .ThenBy(statement => statement.ParameterSet, StringComparer.Ordinal)
            .ThenBy(statement => statement.StatementOrdinal)
            .ToArray();

        return new ArtifactStatementsSection(
            string.Empty,
            artifactKind,
            aggregated.Take(StatementMetricsExtractor.MaximumStatements).ToArray(),
            omittedFromPlans + Math.Max(0, aggregated.Length - StatementMetricsExtractor.MaximumStatements));
    }

    private static long? Median(IEnumerable<long?> values)
    {
        var present = values.OfType<long>().ToArray();
        return present.Length == 0 ? null : Distribution.From(present).Median;
    }
}