using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    long? Executions)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodePath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cursor { get; init; }
}

public sealed record StatementMetrics(
    int StatementOrdinal,
    string StatementHash,
    long? CpuTimeMilliseconds,
    long? ElapsedTimeMilliseconds,
    int? DegreeOfParallelism,
    IReadOnlyList<StatementOperatorSummary> TopOperators)
{
    // Journal-only fields stay absent from the existing artifact wire projection.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Occurrence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Executions { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanHash { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanDocumentOrdinal { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanStatementIndex { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StatementObjectCounters>? Objects { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? UdfElapsedMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HiddenWork>? HiddenWork { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CountersComplete { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IdentityComplete { get; init; }

    [JsonIgnore]
    public string? Text { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DocumentOrdinal { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? StatementId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? StatementCompId { get; init; }

    [JsonIgnore]
    internal IReadOnlyList<StatementMetricSource> Sources { get; init; } = [];
}

internal sealed record StatementMetricSource(int PlanDocumentOrdinal, int PlanStatementIndex);

public sealed record StatementObjectCounters(
    string Object,
    string Kind,
    long LogicalReads,
    long LobLogicalReads,
    long Scans);

public sealed record HiddenWork(string Kind, string Name, long? Executions, double? SharePercent);

/// <summary>Normalizes SQL text for stable statement identity while preserving quoted content.</summary>
internal static class StatementKey
{
    internal static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new StringBuilder(text.Length);
        var pendingSpace = false;
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                pendingSpace = output.Length > 0;
                index++;
                continue;
            }

            if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                index += 2;
                while (index < text.Length && text[index] is not '\r' and not '\n')
                    index++;
                pendingSpace = output.Length > 0;
                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index += 2;
                var depth = 1;
                while (index < text.Length && depth > 0)
                {
                    if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }
                pendingSpace = output.Length > 0;
                continue;
            }

            if (pendingSpace && output.Length > 0)
                output.Append(' ');
            pendingSpace = false;

            if (current is '\'' or '"' or '[')
            {
                var close = current == '[' ? ']' : current;
                output.Append(current);
                index++;
                while (index < text.Length)
                {
                    var quoted = text[index++];
                    output.Append(quoted);
                    if (quoted != close)
                        continue;
                    if (index < text.Length && text[index] == close && current != '[')
                    {
                        output.Append(text[index++]);
                        continue;
                    }
                    if (current == '[' && index < text.Length && text[index] == ']')
                    {
                        output.Append(text[index++]);
                        continue;
                    }
                    break;
                }
                continue;
            }

            output.Append(current);
            index++;
        }

        return output.ToString().Trim();
    }
}

/// <summary>Journal-specific extraction; deliberately independent of the legacy capped artifact projection.</summary>
internal static class JournalStatementMetricsExtractor
{
    internal const int MaximumBytes = 4 * 1024 * 1024;

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    internal static IReadOnlyList<StatementMetrics> Extract(IReadOnlyList<string> documents, out int omittedStatements)
    {
        ArgumentNullException.ThrowIfNull(documents);
        omittedStatements = 0;
        var parsed = new List<ParsedStatement>();
        var documentOrdinal = 0;
        foreach (var document in documents)
        {
            var statements = ReadStatements(document ?? string.Empty);
            for (var statementIndex = 0; statementIndex < statements.Length; statementIndex++)
                parsed.Add(Parse(statements[statementIndex], documentOrdinal, statementIndex) with { PlanOrdinal = parsed.Count });
            documentOrdinal++;
        }

        var aggregateGroups = new List<List<ParsedStatement>>();
        foreach (var compGroup in parsed.Where(item => item.StatementCompId is > 0)
                     .GroupBy(item => item.StatementCompId!.Value))
        {
            var entries = compGroup.ToArray();
            var safe = entries.Length > 0
                && entries.All(item => item.PlanHash is not null)
                && entries.Select(item => item.NormalizedText).Distinct(StringComparer.Ordinal).Count() == 1
                && entries.Select(item => item.PlanHash).Distinct(StringComparer.Ordinal).Count() == 1
                && entries.Select(item => item.DocumentOrdinal).Distinct().Count() == entries.Length;
            if (safe)
                aggregateGroups.Add(entries.ToList());
            else
                aggregateGroups.AddRange(entries.Select(item => new List<ParsedStatement> { item with { IdentityConflicted = true } }));
        }

        var assigned = parsed.Where(item => item.StatementCompId is not > 0)
            .Select(item => new List<ParsedStatement> { item });
        var orderedGroups = aggregateGroups.Concat(assigned)
            .OrderBy(group => group.Min(item => item.PlanOrdinal))
            .ToArray();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<StatementMetrics>(orderedGroups.Length);
        var consumedBytes = 0;
        for (var groupIndex = 0; groupIndex < orderedGroups.Length; groupIndex++)
        {
            var group = orderedGroups[groupIndex];
            var first = group.OrderBy(item => item.PlanOrdinal).First();
            var key = PlanIdentity.StatementSha256(first.NormalizedText);
            occurrences.TryGetValue(key, out var occurrence);
            occurrences[key] = occurrence + 1;
            var metric = ToMetrics(group, groupIndex, key, occurrence);
            var metricBytes = JsonSerializer.SerializeToUtf8Bytes(metric).Length
                + Encoding.UTF8.GetByteCount(metric.Text ?? string.Empty)
                + 2;
            if (metricBytes > MaximumBytes - consumedBytes)
            {
                omittedStatements++;
                continue;
            }

            consumedBytes += metricBytes;
            output.Add(metric);
        }

        return output;
    }

    private static XElement[] ReadStatements(string document)
    {
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
            return xml.Root?.Name == Showplan + "ShowPlanXML"
                ? xml.Descendants(Showplan + "StmtSimple").ToArray()
                : [];
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or FormatException or OverflowException)
        {
            return [];
        }
    }

    private static ParsedStatement Parse(XElement statement, int documentOrdinal, int statementIndex)
    {
        var queryPlan = statement.Element(Showplan + "QueryPlan");
        var text = Attribute(statement, "StatementText") ?? string.Empty;
        var normalizedText = StatementKey.Normalize(text);
        var root = queryPlan?.Element(Showplan + "RelOp");
        var rootCounters = root?.DescendantsAndSelf().Where(element => element.Name == Showplan + "RunTimeCountersPerThread"
            && (element == root || element.Ancestors(Showplan + "RelOp").FirstOrDefault() == root)).ToArray() ?? [];
        var (executions, _) = SumCounters(rootCounters, "ActualExecutions");
        var countersComplete = root is not null;
        var objectValues = new Dictionary<string, MutableObjectCounters>(StringComparer.Ordinal);
        var tvfValues = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var relOp in queryPlan?.Descendants(Showplan + "RelOp") ?? [])
        {
            var own = relOp.DescendantsAndSelf().Where(element => element == relOp
                || element.Ancestors(Showplan + "RelOp").FirstOrDefault() == relOp).ToArray();
            var objectElement = own.FirstOrDefault(element => element.Name == Showplan + "Object");
            var table = Attribute(objectElement, "Table");
            var runtime = own.Where(element => element.Name == Showplan + "RunTimeCountersPerThread").ToArray();
            var reads = SumCounters(runtime, "ActualLogicalReads");
            var lobReads = SumCounters(runtime, "ActualLobLogicalReads");
            var scans = SumCounters(runtime, "ActualScans");
            var physicalOp = Attribute(relOp, "PhysicalOp") ?? string.Empty;
            if (table is null)
            {
                var hasUnattributedIo = reads.Value.GetValueOrDefault() > 0
                    || lobReads.Value.GetValueOrDefault() > 0
                    || scans.Value.GetValueOrDefault() > 0;
                var missingRuntimeIo = !reads.Complete || !lobReads.Complete || !scans.Complete;
                // These object-free operators can omit their own IO counters; child objects carry their IO.
                var objectFreeOperatorHasNoIoCounters = physicalOp.Equals("Compute Scalar", StringComparison.OrdinalIgnoreCase)
                    || physicalOp.Equals("Nested Loops", StringComparison.OrdinalIgnoreCase)
                    || physicalOp.Equals("Stream Aggregate", StringComparison.OrdinalIgnoreCase);
                if (hasUnattributedIo || missingRuntimeIo && !objectFreeOperatorHasNoIoCounters)
                    countersComplete = false;
            }
            else
            {
                if (!reads.Complete || !lobReads.Complete || !scans.Complete)
                    countersComplete = false;
                if (!objectValues.TryGetValue(table, out var counters))
                {
                    counters = new MutableObjectCounters(table, Kind(table));
                    objectValues.Add(table, counters);
                }
                counters.Add(reads.Value, lobReads.Value, scans.Value, reads.Complete && lobReads.Complete && scans.Complete);
            }

            var logicalOp = Attribute(relOp, "LogicalOp") ?? string.Empty;
            if (physicalOp.Contains("table-valued function", StringComparison.OrdinalIgnoreCase)
                || logicalOp.Contains("table-valued function", StringComparison.OrdinalIgnoreCase))
            {
                var name = table ?? physicalOp;
                var (tvfExecutions, tvfComplete) = SumCounters(runtime, "ActualExecutions");
                if (!tvfComplete)
                    countersComplete = false;
                var priorExecutions = tvfValues.TryGetValue(name, out var foundExecutions) ? foundExecutions : 0L;
                tvfValues[name] = priorExecutions.HasValue && tvfExecutions.HasValue
                    ? SaturatingAdd(priorExecutions.Value, tvfExecutions.Value)
                    : null;
            }
        }

        var udfValues = statement.DescendantsAndSelf()
            .Select(element => LongValue(Attribute(element, "UdfElapsedTime")))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        long? udfElapsed = udfValues.Length == 0 ? null : udfValues.Aggregate(0L, SaturatingAdd);
        var cpu = LongValue(Attribute(queryPlan?.Element(Showplan + "QueryTimeStats"), "CpuTime"));
        var elapsed = LongValue(Attribute(queryPlan?.Element(Showplan + "QueryTimeStats"), "ElapsedTime"));
        double? udfShare = udfElapsed.HasValue && elapsed.HasValue && elapsed.Value > 0
            ? Math.Round(udfElapsed.Value * 100d / elapsed.Value, 1)
            : null;
        var hidden = new List<HiddenWork>();
        if (udfElapsed is > 0)
        {
            var name = statement.DescendantsAndSelf()
                .Select(element => Attribute(element, "FunctionName"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "scalar UDF";
            hidden.Add(new HiddenWork("scalar-udf", name, null, udfShare));
        }
        hidden.AddRange(tvfValues.Select(value => new HiddenWork("multi-statement-tvf", value.Key, value.Value, null)));
        return new ParsedStatement(
            documentOrdinal,
            statementIndex,
            text,
            normalizedText,
            ParseInt(Attribute(statement, "StatementId")),
            ParseInt(Attribute(statement, "StatementCompId")),
            Attribute(statement, "QueryPlanHash"),
            ParseInt(Attribute(queryPlan, "DegreeOfParallelism")),
            executions,
            countersComplete,
            objectValues,
            cpu,
            udfElapsed,
            elapsed,
            hidden);
    }

    private static StatementMetrics ToMetrics(IReadOnlyList<ParsedStatement> group, int ordinal, string key, int occurrence)
    {
        var first = group[0];
        var complete = group.All(item => item.CountersComplete);
        var objects = new Dictionary<string, MutableObjectCounters>(StringComparer.Ordinal);
        foreach (var item in group)
        {
            foreach (var source in item.Objects.Values)
            {
                if (!objects.TryGetValue(source.Object, out var target))
                {
                    target = new MutableObjectCounters(source.Object, source.Kind);
                    objects.Add(source.Object, target);
                }
                target.Add(source.LogicalReads, source.LobLogicalReads, source.Scans, source.Complete);
            }
        }

        long? executions = group.All(item => item.Executions.HasValue)
            ? group.Select(item => item.Executions!.Value).Aggregate(0L, SaturatingAdd)
            : null;
        long? cpu = group.All(item => item.Cpu.HasValue)
            ? group.Select(item => item.Cpu!.Value).Aggregate(0L, SaturatingAdd)
            : null;
        long? udfElapsed = group.All(item => item.UdfElapsed.HasValue)
            ? group.Select(item => item.UdfElapsed!.Value).Aggregate(0L, SaturatingAdd)
            : null;
        long? elapsed = group.All(item => item.Elapsed.HasValue)
            ? group.Select(item => item.Elapsed!.Value).Aggregate(0L, SaturatingAdd)
            : null;
        var hidden = new List<HiddenWork>();
        if (udfElapsed is > 0)
            hidden.Add(new HiddenWork("scalar-udf", first.Hidden.FirstOrDefault(work => work.Kind == "scalar-udf")?.Name ?? "scalar UDF", null,
                elapsed is > 0 ? Math.Round(udfElapsed.Value * 100d / elapsed.Value, 1) : null));
        foreach (var tvf in group.SelectMany(item => item.Hidden.Where(work => work.Kind == "multi-statement-tvf"))
                     .GroupBy(work => work.Name, StringComparer.Ordinal))
        {
            var values = tvf.Select(work => work.Executions).ToArray();
            hidden.Add(new HiddenWork("multi-statement-tvf", tvf.Key,
                values.All(value => value.HasValue) ? values.Select(value => value!.Value).Aggregate(0L, SaturatingAdd) : null,
                null));
        }

        return new StatementMetrics(
            ordinal,
            PlanIdentity.StatementSha256(first.Text),
            cpu,
            elapsed,
            first.DegreeOfParallelism,
            [])
        {
            Key = key,
            Occurrence = occurrence,
            Executions = executions,
            PlanHash = first.PlanHash,
            Objects = objects.Values.Select(item => item.ToRecord()).ToArray(),
            UdfElapsedMs = udfElapsed,
            HiddenWork = hidden,
            CountersComplete = complete && objects.Values.All(item => item.Complete),
            IdentityComplete = group.All(item => item.StatementCompId is > 0 && item.PlanHash is not null && !item.IdentityConflicted)
                && (group.Count == 1 || group.Select(item => item.DocumentOrdinal).Distinct().Count() == group.Count),
            Text = first.Text,
            DocumentOrdinal = first.DocumentOrdinal,
            PlanDocumentOrdinal = first.DocumentOrdinal,
            PlanStatementIndex = first.StatementIndex,
            Sources = group.Select(item => new StatementMetricSource(item.DocumentOrdinal, item.StatementIndex)).ToArray(),
            StatementId = first.StatementId,
            StatementCompId = first.StatementCompId,
        };
    }

    private static (long? Value, bool Complete) SumCounters(IReadOnlyList<XElement> elements, string name)
    {
        if (elements.Count == 0)
            return (null, false);
        long total = 0;
        foreach (var element in elements)
        {
            var value = LongValue(Attribute(element, name));
            if (!value.HasValue)
                return (null, false);
            total = SaturatingAdd(total, value.Value);
        }
        return (total, true);
    }

    private static string? Attribute(XElement? element, string name) => element?.Attribute(name)?.Value;

    private static int? ParseInt(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    private static long? LongValue(string? text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    private static string Kind(string table) => table.Equals("Worktable", StringComparison.OrdinalIgnoreCase)
        || table.Equals("[Worktable]", StringComparison.OrdinalIgnoreCase)
        || table.Equals("Workfile", StringComparison.OrdinalIgnoreCase)
        || table.Equals("[Workfile]", StringComparison.OrdinalIgnoreCase)
            ? "worktable"
            : table.StartsWith("[@", StringComparison.Ordinal) ? "table-variable"
            : table.StartsWith("[#", StringComparison.Ordinal) ? "temp"
            : "table";

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed record ParsedStatement(
        int DocumentOrdinal,
        int StatementIndex,
        string Text,
        string NormalizedText,
        int? StatementId,
        int? StatementCompId,
        string? PlanHash,
        int? DegreeOfParallelism,
        long? Executions,
        bool CountersComplete,
        Dictionary<string, MutableObjectCounters> Objects,
        long? Cpu,
        long? UdfElapsed,
        long? Elapsed,
        IReadOnlyList<HiddenWork> Hidden)
    {
        public int PlanOrdinal { get; init; }
        public bool IdentityConflicted { get; init; }
    }

    private sealed class MutableObjectCounters(string objectName, string kind)
    {
        public string Object { get; } = objectName;
        public string Kind { get; } = kind;
        public long LogicalReads { get; private set; }
        public long LobLogicalReads { get; private set; }
        public long Scans { get; private set; }
        public bool Complete { get; private set; } = true;

        public void Add(long? reads, long? lobReads, long? scans, bool complete)
        {
            LogicalReads = SaturatingAdd(LogicalReads, reads.GetValueOrDefault());
            LobLogicalReads = SaturatingAdd(LobLogicalReads, lobReads.GetValueOrDefault());
            Scans = SaturatingAdd(Scans, scans.GetValueOrDefault());
            Complete &= complete;
        }

        public StatementObjectCounters ToRecord() => new(Object, Kind, LogicalReads, LobLogicalReads, Scans);
    }
}

/// <summary>Extracts bounded per-statement diagnostics from an actual SQL Server showplan.</summary>
internal static class StatementMetricsExtractor
{
    internal const int MaximumStatements = 128;
    internal const int MaximumOperators = 10;
    internal const int MaximumTextCharacters = 512;

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    internal static IReadOnlyList<StatementMetrics> Extract(string document, out int omittedStatements) =>
        Extract(document, null, null, out omittedStatements);

    internal static IReadOnlyList<StatementMetrics> Extract(
        string document,
        string? executedSql,
        IReadOnlyList<StatementSpanPair>? pairs,
        out int omittedStatements)
    {
        omittedStatements = 0;
        var statements = ReadStatements(document);
        if (statements.Length == 0)
            return [];
        omittedStatements = Math.Max(0, statements.Length - MaximumStatements);
        return statements.Take(MaximumStatements).Select((statement, ordinal) => Parse(
            statement, ordinal, executedSql,
            pairs is not null && ordinal < pairs.Count ? pairs[ordinal] : null)).ToArray();
    }

    internal static int CountStatements(string document) => ReadStatements(document).Length;

    private static XElement[] ReadStatements(string document)
    {
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
            return xml.Root?.Name == Showplan + "ShowPlanXML"
                ? xml.Descendants(Showplan + "StmtSimple").ToArray()
                : [];
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or FormatException or OverflowException)
        {
            return [];
        }
    }

    private static StatementMetrics Parse(XElement statement, int ordinal, string? executedSql, StatementSpanPair? pair)
    {
        var queryPlan = statement.Element(Showplan + "QueryPlan");
        var text = Attribute(statement, "StatementText") ?? string.Empty;
        var identityText = pair is { SpanStart: int start, SpanLength: int length } && executedSql is not null
            ? executedSql.Substring(start, length)
            : text;
        var walked = new List<StatementOperatorSummary>();
        var root = queryPlan?.Element(Showplan + "RelOp");
        if (root is not null)
        {
            var cursor = 0;
            WalkOperators(root, "0", walked, ref cursor);
        }
        var operators = walked
            .OrderByDescending(item => item.ActualRows.HasValue)
            .ThenByDescending(item => item.ActualRows)
            .ThenByDescending(item => item.Executions)
            .ThenBy(item => item.NodeId)
            .Take(MaximumOperators)
            .ToArray();

        return new StatementMetrics(
            ordinal,
            PlanIdentity.StatementSha256(identityText),
            Long(queryPlan?.Element(Showplan + "QueryTimeStats"), "CpuTime"),
            Long(queryPlan?.Element(Showplan + "QueryTimeStats"), "ElapsedTime"),
            Int(queryPlan, "DegreeOfParallelism"),
            operators);
    }

    private static StatementOperatorSummary ParseOperator(XElement relOp)
    {
        var own = relOp.DescendantsAndSelf().Where(element => element == relOp
            || element.Ancestors(Showplan + "RelOp").FirstOrDefault() == relOp).ToArray();
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

    private static void WalkOperators(XElement relOp, string path, List<StatementOperatorSummary> operators, ref int cursor)
    {
        operators.Add(ParseOperator(relOp) with { NodePath = path, Cursor = cursor });
        cursor++;
        var index = 0;
        foreach (var child in DirectChildOperators(relOp))
        {
            WalkOperators(child, path + "/" + index, operators, ref cursor);
            index++;
        }
    }

    private static IEnumerable<XElement> DirectChildOperators(XElement relOp)
    {
        foreach (var child in relOp.Elements())
        {
            if (child.Name == Showplan + "RelOp")
            {
                yield return child;
                continue;
            }

            foreach (var descendant in DirectChildOperators(child))
                yield return descendant;
        }
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
    private const int MaximumEvidenceStatements = 512;
    private const int MaximumHiddenWorkItemsPerStatement = 16;
    private const int MaximumEvidenceTextBytes = 512;

    internal static ArtifactStatementsSection Project(string artifactKind, IReadOnlyList<CompareRunArtifact> runs)
    {
        var extracted = new List<ArtifactStatementMetric>();
        var omitted = 0;
        var omittedByCase = new Dictionary<(string Variant, string? ParameterSet, int? MatrixCell), int>();
        var runDiagnostics = new List<ArtifactRunPlanDiagnostic>();
        var diagnosticCases = new List<ArtifactPlanDiagnosticAvailability>();
        var diagnosticSourceCount = 0;
        foreach (var run in runs)
        {
            var caseKey = (run.Variant, run.ParameterSet, run.MatrixCell);
            var diagnosticSources = PlanMetricsExtractor.ExtractDiagnosticSources(run.PlanXmls);
            diagnosticSourceCount += diagnosticSources.Count;
            diagnosticCases.Add(new ArtifactPlanDiagnosticAvailability(
                run.Variant, run.ParameterSet, run.MatrixCell, run.Repetition,
                diagnosticSources.Count == 0 ? "unavailable" : "available",
                diagnosticSources.Count == 0 ? null : "sql-server-showplan-query-plan"));
            runDiagnostics.AddRange(diagnosticSources
                .Where(source => source.Scope == "batch")
                .Select(source => new ArtifactRunPlanDiagnostic(
                    run.Variant, run.ParameterSet, run.MatrixCell, run.Repetition, ToArtifactDiagnostic(source))));
            var metrics = JournalStatementMetricsExtractor.Extract(run.PlanXmls, out var omittedFromRun);
            omitted += omittedFromRun;
            if (omittedFromRun > 0)
                omittedByCase[caseKey] = omittedByCase.GetValueOrDefault(caseKey) + omittedFromRun;
            var statuses = StatementReconciler.Reconcile(metrics, run.TableIo);
            var legacyMetrics = LegacyMetrics(run);
            for (var index = 0; index < metrics.Count; index++)
            {
                if (extracted.Count == MaximumEvidenceStatements)
                {
                    var omittedForRun = metrics.Count - index;
                    omitted += omittedForRun;
                    omittedByCase[caseKey] = omittedByCase.GetValueOrDefault(caseKey) + omittedForRun;
                    break;
                }

                var metric = metrics[index];
                var attribution = omittedFromRun > 0 ? StatementStatus.Partial : statuses[index];
                legacyMetrics.TryGetValue((metric.PlanDocumentOrdinal, metric.PlanStatementIndex), out var legacyMetric);
                var statementDiagnostics = diagnosticSources
                    .Where(source => source.Scope == "statement"
                        && metric.Sources.Any(location => location.PlanDocumentOrdinal == source.PlanDocumentOrdinal
                            && location.PlanStatementIndex == source.PlanStatementIndex))
                    .ToArray();
                var allHidden = (metric.HiddenWork ?? [])
                    .Where(work => work.Kind is "scalar-udf" or "multi-statement-tvf")
                    .ToArray();
                var hidden = allHidden
                    .Take(MaximumHiddenWorkItemsPerStatement)
                    .Select(work =>
                    {
                        var (name, truncated) = Clip(work.Name);
                        return new ArtifactHiddenWork(work.Kind, name, truncated, work.Executions, work.SharePercent);
                    })
                    .ToArray();
                extracted.Add(new ArtifactStatementMetric(
                    run.Variant,
                    run.ParameterSet,
                    run.MatrixCell,
                    metric.StatementOrdinal,
                    legacyMetric?.StatementHash ?? metric.StatementHash,
                    metric.CpuTimeMilliseconds,
                    metric.ElapsedTimeMilliseconds,
                    metric.DegreeOfParallelism,
                    legacyMetric?.TopOperators ?? metric.TopOperators)
                {
                    Repetition = run.Repetition,
                    CounterAttribution = AttributionText(StatementAttributionClassifier.Classify(attribution)),
                    StatementIdentityComplete = metric.IdentityComplete == true,
                    PlanDocumentOrdinal = metric.PlanDocumentOrdinal,
                    PlanStatementIndex = metric.PlanStatementIndex,
                    PlanHash = metric.PlanHash,
                    Executions = metric.Executions,
                    UdfElapsedMilliseconds = metric.UdfElapsedMs,
                    HiddenWork = hidden,
                    HiddenWorkOmitted = allHidden.Length - hidden.Length,
                    Limitations = Limitations(allHidden),
                    PlanDiagnostics = statementDiagnostics.Take(16).Select(ToArtifactDiagnostic).ToArray(),
                    OmittedPlanDiagnostics = statementDiagnostics.Length > 16 ? statementDiagnostics.Length - 16 : null,
                });
            }
        }
        var ordered = extracted
            .OrderBy(statement => statement.Variant, StringComparer.Ordinal)
            .ThenBy(statement => statement.MatrixCell)
            .ThenBy(statement => statement.ParameterSet, StringComparer.Ordinal)
            .ThenBy(statement => statement.Repetition)
            .ThenBy(statement => statement.StatementOrdinal)
            .ToArray();

        return new ArtifactStatementsSection(
            string.Empty,
            artifactKind,
            ordered,
            omitted)
        {
            EvidenceVersion = 1,
            EvidenceAvailability = ordered.Length == 0 ? "unavailable" : "available",
            CoverageBasis = "collected-statement-counters",
            CoverageDescription = "Attribution describes collected statement-counter coverage only; it does not establish complete query cost.",
            PlanDiagnosticsAvailability = diagnosticSourceCount == 0 ? "unavailable" : "available",
            RunPlanDiagnostics = runDiagnostics.Take(512).ToArray(),
            PlanDiagnosticCases = diagnosticCases.Take(512).ToArray(),
            OmittedRunPlanDiagnostics = Math.Max(0, runDiagnostics.Count - 512),
            OmittedPlanDiagnosticCases = Math.Max(0, diagnosticCases.Count - 512),
            OmittedByCase = omittedByCase.Select(pair => new ArtifactStatementOmission(
                pair.Key.Variant, pair.Key.ParameterSet, pair.Key.MatrixCell, pair.Value)).ToArray(),
        };
    }

    private static ArtifactPlanDiagnostic ToArtifactDiagnostic(PlanDiagnosticSource source) => new(
        "sql-server-showplan-query-plan",
        source.Scope,
        source.PlanDocumentOrdinal,
        source.PlanStatementIndex,
        source.DegreeOfParallelism,
        source.GrantRequestedKb,
        source.GrantGrantedKb,
        source.GrantMaxUsedKb,
        source.CompileTimeMs,
        source.CompileCpuMs);

    private static IReadOnlyList<string> Limitations(IReadOnlyList<HiddenWork> hidden)
    {
        if (hidden.Count == 0) return [];
        var limitations = new List<string>();
        if (hidden.Any(work => work.Kind == "scalar-udf"))
            limitations.Add("Scalar UDF work may not be fully represented by collected statement counters.");
        if (hidden.Any(work => work.Kind == "multi-statement-tvf"))
            limitations.Add("Multi-statement TVF work may not be fully represented by collected statement counters.");
        return limitations;
    }

    private static string AttributionText(StatementCounterAttribution attribution) => attribution switch
    {
        StatementCounterAttribution.Complete => "complete",
        StatementCounterAttribution.Partial => "partial",
        _ => "unavailable",
    };

    private static (string Value, bool Truncated) Clip(string value)
    {
        var output = new StringBuilder(Math.Min(value.Length, MaximumEvidenceTextBytes));
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > MaximumEvidenceTextBytes)
                return (output.ToString(), true);
            output.Append(rune.ToString());
            bytes += runeBytes;
        }
        return (output.ToString(), false);
    }

    private static IReadOnlyDictionary<(int? DocumentOrdinal, int? StatementIndex), StatementMetrics> LegacyMetrics(CompareRunArtifact run)
    {
        var result = new Dictionary<(int? DocumentOrdinal, int? StatementIndex), StatementMetrics>();
        var pairsByDocument = StatementSpanPairer.PairDocuments(run.ExecutedSql, run.PlanXmls);
        for (var documentIndex = 0; documentIndex < run.PlanXmls.Count; documentIndex++)
        {
            var statements = StatementMetricsExtractor.Extract(
                run.PlanXmls[documentIndex], run.ExecutedSql, pairsByDocument[documentIndex], out _);
            foreach (var statement in statements)
                result.TryAdd((documentIndex, statement.StatementOrdinal), statement);
        }
        return result;
    }
}
