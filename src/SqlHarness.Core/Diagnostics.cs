using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SqlHarness.Core;

internal sealed record StatisticsIo(long LogicalReads, IReadOnlyDictionary<string, long> Tables);

internal static class StatisticsIoParser
{
    private static readonly Regex IoLine = new(
        @"Table\s+'(?<table>(?:''|[^'])+)'\.[^\r\n]*?\blogical reads\s+(?<logical>\d+)(?<tail>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LobLogicalReads = new(
        @"\blob logical reads\s+(?<logical>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static StatisticsIo Parse(string text)
    {
        var tables = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (Match match in IoLine.Matches(text))
        {
            var table = match.Groups["table"].Value.Replace("''", "'", StringComparison.Ordinal);
            var reads = ParseLong(match.Groups["logical"].Value);
            var lobMatch = LobLogicalReads.Match(match.Groups["tail"].Value);
            if (lobMatch.Success)
            {
                reads += ParseLong(lobMatch.Groups["logical"].Value);
            }

            tables[table] = tables.GetValueOrDefault(table) + reads;
        }

        return new StatisticsIo(tables.Values.Sum(), tables);
    }

    private static long ParseLong(string value) => long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}

internal sealed record StatisticsTime(long CpuTimeMs, long ElapsedTimeMs);

internal static class StatisticsTimeParser
{
    private static readonly Regex ExecutionTime = new(
        @"SQL Server Execution Times:\s*CPU time\s*=\s*(?<cpu>\d+)\s*ms,\s*elapsed time\s*=\s*(?<elapsed>\d+)\s*ms\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static StatisticsTime Parse(string text)
    {
        long cpuTimeMs = 0;
        long elapsedTimeMs = 0;

        foreach (Match match in ExecutionTime.Matches(text))
        {
            cpuTimeMs += long.Parse(match.Groups["cpu"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            elapsedTimeMs += long.Parse(match.Groups["elapsed"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return new StatisticsTime(cpuTimeMs, elapsedTimeMs);
    }
}

internal sealed record Distribution(long Min, long Median, long Max)
{
    internal static Distribution From(IEnumerable<long> samples)
    {
        var ordered = samples.Order().ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        }

        var middle = ordered.Length / 2;
        var median = ordered.Length % 2 == 0
            ? (long)(((decimal)ordered[middle - 1] + ordered[middle]) / 2)
            : ordered[middle];

        return new Distribution(ordered[0], median, ordered[^1]);
    }
}

internal sealed record ExecutionPlan(IReadOnlyList<PlanOperator> Operators);

internal sealed record PlanOperator(
    int NodeId,
    string PhysicalOp,
    string? Object,
    bool HasWarnings,
    bool HasSpill,
    bool HasImplicitConversion);

internal static class ExecutionPlanParser
{
    internal static ExecutionPlan Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var operators = document
            .Descendants()
            .Where(element => element.Name.LocalName == "RelOp")
            .Select(ParseOperator)
            .ToArray();

        return new ExecutionPlan(operators);
    }

    private static PlanOperator ParseOperator(XElement relOp)
    {
        var ownedElements = OwnedElements(relOp).ToArray();
        var objectElement = ownedElements.FirstOrDefault(element => element.Name.LocalName == "Object");
        var objectName = objectElement is null
            ? null
            : Identifier(Attribute(objectElement, "Table"));

        return new PlanOperator(
            int.Parse(Attribute(relOp, "NodeId")!, NumberStyles.None, CultureInfo.InvariantCulture),
            Attribute(relOp, "PhysicalOp") ?? string.Empty,
            objectName,
            ownedElements.Any(element => element.Name.LocalName == "Warnings"),
            ownedElements.Any(element => element.Name.LocalName == "SpillToTempDb"),
            ownedElements.Any(IsImplicitConversion));
    }

    private static IEnumerable<XElement> OwnedElements(XElement relOp)
    {
        foreach (var child in relOp.Elements())
        {
            if (child.Name.LocalName == "RelOp")
            {
                continue;
            }

            yield return child;
            foreach (var descendant in OwnedElements(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsImplicitConversion(XElement element) =>
        element.Name.LocalName == "PlanAffectingConvert"
        || element.Attributes().Any(attribute =>
            attribute.Value.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase));

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static string? Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split('.', StringSplitOptions.RemoveEmptyEntries)[^1].Trim('[', ']');
    }
}