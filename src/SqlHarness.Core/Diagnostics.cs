using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SqlHarness.Core;

internal sealed record StatisticsIo(long LogicalReads, IReadOnlyDictionary<string, long> Tables);

internal static partial class TempTableKey
{
    internal static string Normalize(string table)
    {
        var bracketed = Bracketed().Match(table);
        if (bracketed.Success)
            return bracketed.Groups["head"].Value + "]";
        var bare = Bare().Match(table);
        return bare.Success ? bare.Groups["head"].Value : table;
    }

    [GeneratedRegex(@"^(?<head>(?:\[[^\]]+\]\.)?\[#.+?)_{2,}[0-9A-Fa-f]{12}\]$", RegexOptions.CultureInvariant)]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"^(?<head>#.+?)_{2,}[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex Bare();
}

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
            var table = TempTableKey.Normalize(match.Groups["table"].Value.Replace("''", "'", StringComparison.Ordinal));
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

internal sealed record TableIoCounters(
    string Table,
    long ScanCount,
    long LogicalReads,
    long PhysicalReads,
    long PageServerReads,
    long ReadAheadReads,
    long LobLogicalReads,
    long LobPhysicalReads,
    long LobReadAheadReads);

/// <summary>
/// Per-table SQL Server STATISTICS IO counters for the activity journal only:
/// scan count and logical, physical, page server, read-ahead, and LOB
/// logical/physical/read-ahead reads. Page server read-ahead and LOB page
/// server counters are not kept.
/// Missing counters (older servers) read as zero; a table repeated across
/// statements is summed. Agent reports keep using <see cref="StatisticsIoParser"/>.
/// </summary>
internal static class StatisticsIoDetailParser
{
    private static readonly Regex TableLine = new(
        @"Table\s+'(?<table>(?:''|[^'])+)'\.(?<counters>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Counter = new(
        @"^(?<name>[A-Za-z][A-Za-z -]*?)\s+(?<value>\d+)\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<TableIoCounters> Parse(string text)
    {
        var tables = new Dictionary<string, TableIoCounters>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (Match line in TableLine.Matches(text))
        {
            var table = TempTableKey.Normalize(line.Groups["table"].Value.Replace("''", "'", StringComparison.Ordinal));
            var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in line.Groups["counters"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Counter.Match(segment);
                if (match.Success && long.TryParse(match.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    values[match.Groups["name"].Value.Trim()] = value;
            }

            // Like StatisticsIoParser, a line without logical reads (localized output,
            // columnstore segment lines) is not a counter line; recording it would turn unknown into zero.
            if (!values.ContainsKey("logical reads"))
                continue;

            long Get(string name) => values.GetValueOrDefault(name);
            var counters = new TableIoCounters(
                table,
                Get("scan count"),
                Get("logical reads"),
                Get("physical reads"),
                Get("page server reads"),
                Get("read-ahead reads"),
                Get("lob logical reads"),
                Get("lob physical reads"),
                Get("lob read-ahead reads"));
            if (tables.TryGetValue(table, out var existing))
            {
                tables[table] = existing with
                {
                    ScanCount = existing.ScanCount + counters.ScanCount,
                    LogicalReads = existing.LogicalReads + counters.LogicalReads,
                    PhysicalReads = existing.PhysicalReads + counters.PhysicalReads,
                    PageServerReads = existing.PageServerReads + counters.PageServerReads,
                    ReadAheadReads = existing.ReadAheadReads + counters.ReadAheadReads,
                    LobLogicalReads = existing.LobLogicalReads + counters.LobLogicalReads,
                    LobPhysicalReads = existing.LobPhysicalReads + counters.LobPhysicalReads,
                    LobReadAheadReads = existing.LobReadAheadReads + counters.LobReadAheadReads,
                };
            }
            else
            {
                tables[table] = counters;
                order.Add(table);
            }
        }

        return order.Select(table => tables[table]).ToArray();
    }
}

internal sealed record StatisticsTime(long CpuTimeMs, long ElapsedTimeMs, int RecognizedBlocks);

internal static class StatisticsTimeParser
{
    private static readonly Regex ExecutionTime = new(
        @"SQL Server Execution Times:\s*CPU time\s*=\s*(?<cpu>\d+)\s*ms,\s*elapsed time\s*=\s*(?<elapsed>\d+)\s*ms\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static StatisticsTime Parse(string text)
    {
        long cpuTimeMs = 0;
        long elapsedTimeMs = 0;
        var matches = ExecutionTime.Matches(text);

        foreach (Match match in matches)
        {
            cpuTimeMs += long.Parse(match.Groups["cpu"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            elapsedTimeMs += long.Parse(match.Groups["elapsed"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return new StatisticsTime(cpuTimeMs, elapsedTimeMs, matches.Count);
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

internal sealed record ExecutionPlan(IReadOnlyList<PlanOperator> Operators, int StatementCount = 1);

internal sealed record PlanOperator(
    int NodeId,
    string PhysicalOp,
    string? Object,
    bool HasWarnings,
    bool HasSpill,
    bool HasImplicitConversion)
{
    public int StatementOrdinal { get; init; }

    public string? NodePath { get; init; }

    public int? Cursor { get; init; }
}

internal static class ExecutionPlanParser
{
    internal static ExecutionPlan Parse(string xml)
    {
        var trimmed = xml.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return ParseJson(xml);

        var document = XDocument.Parse(xml);
        var operators = new List<PlanOperator>();
        var statements = document
            .Descendants()
            .Where(element => element.Name.LocalName == "StmtSimple")
            .ToArray();
        for (var ordinal = 0; ordinal < statements.Length; ordinal++)
        {
            var queryPlan = statements[ordinal].Elements().FirstOrDefault(element => element.Name.LocalName == "QueryPlan");
            var root = queryPlan?.Elements().FirstOrDefault(element => element.Name.LocalName == "RelOp");
            if (root is null)
                continue;
            var cursor = 0;
            WalkRelOp(root, ordinal, "0", operators, ref cursor);
        }

        return new ExecutionPlan(operators, statements.Length);
    }

    private static ExecutionPlan ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json, Postgres.PostgresBenchmark.PlanJsonOptions);
        var operators = new List<PlanOperator>();
        var statementCount = 0;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("Plan", out var plan))
                    continue;
                var cursor = 0;
                WalkJson(plan, statementCount, "0", operators, ref cursor);
                statementCount++;
            }
        }
        else if (document.RootElement.TryGetProperty("Plan", out var plan))
        {
            var cursor = 0;
            WalkJson(plan, 0, "0", operators, ref cursor);
            statementCount = 1;
        }

        return new ExecutionPlan(operators, statementCount == 0 ? 1 : statementCount);
    }

    private static void WalkJson(JsonElement node, int statementOrdinal, string path, List<PlanOperator> operators, ref int cursor)
    {
        var physicalOp = node.TryGetProperty("Node Type", out var nodeType) && nodeType.ValueKind == JsonValueKind.String
            ? nodeType.GetString() ?? string.Empty
            : string.Empty;
        var relation = Postgres.PostgresPlanDistiller.QualifiedRelationName(
            JsonString(node, "Schema"),
            JsonString(node, "Relation Name"));
        var hasWarnings = node.TryGetProperty("Warnings", out _)
            || (node.TryGetProperty("Never Executed", out var neverExecuted) && neverExecuted.ValueKind == JsonValueKind.True);
        operators.Add(new PlanOperator(operators.Count + 1, physicalOp, relation, hasWarnings, false, false)
        {
            StatementOrdinal = statementOrdinal,
            NodePath = path,
            Cursor = cursor,
        });
        cursor++;
        if (node.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in plans.EnumerateArray())
            {
                WalkJson(child, statementOrdinal, path + "/" + index, operators, ref cursor);
                index++;
            }
        }
    }

    private static void WalkRelOp(XElement relOp, int statementOrdinal, string path, List<PlanOperator> operators, ref int cursor)
    {
        operators.Add(ParseOperator(relOp) with
        {
            StatementOrdinal = statementOrdinal,
            NodePath = path,
            Cursor = cursor,
        });
        cursor++;
        var index = 0;
        foreach (var child in DirectChildOperators(relOp))
        {
            WalkRelOp(child, statementOrdinal, path + "/" + index, operators, ref cursor);
            index++;
        }
    }

    private static IEnumerable<XElement> DirectChildOperators(XElement relOp)
    {
        foreach (var child in relOp.Elements())
        {
            if (child.Name.LocalName == "RelOp")
            {
                yield return child;
                continue;
            }

            foreach (var descendant in DirectChildOperators(child))
                yield return descendant;
        }
    }

    private static string? JsonString(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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