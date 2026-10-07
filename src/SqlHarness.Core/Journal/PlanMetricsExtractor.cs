using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SqlHarness.Core;

public sealed record PlanWait(string WaitType, long WaitTimeMs, long WaitCount);

public sealed record PostgresBufferCounters(
    long SharedHit, long SharedRead, long SharedDirtied, long SharedWritten, long TempRead, long TempWritten);

/// <summary>Numeric plan diagnostics only: no statement text, predicates, or parameter values.</summary>
public sealed record PlanMetrics(
    long? GrantRequestedKb,
    long? GrantGrantedKb,
    long? GrantMaxUsedKb,
    int? Dop,
    long? CompileTimeMs,
    long? CompileCpuMs,
    int SpillCount,
    bool HasWarnings,
    bool HasImplicitConversion,
    int MissingIndexCount,
    IReadOnlyList<PlanWait> Waits,
    PostgresBufferCounters? Postgres)
{
    public static PlanMetrics Empty { get; } = new(null, null, null, null, null, null, 0, false, false, 0, [], null);
}

/// <summary>
/// Journal-only plan diagnostics from actual Showplan XML (SQL Server) or
/// EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) (Postgres). Never throws: an
/// unreadable document yields <see cref="PlanMetrics.Empty"/>.
/// </summary>
internal static class PlanMetricsExtractor
{
    internal const int MaximumCharacters = 16 * 1024 * 1024;

    internal static PlanMetrics Extract(IEnumerable<string> documents)
    {
        var parts = documents.Select(Extract).Where(part => !ReferenceEquals(part, PlanMetrics.Empty)).ToArray();
        if (parts.Length == 0)
            return PlanMetrics.Empty;
        if (parts.Length == 1)
            return parts[0];

        var waits = parts.SelectMany(part => part.Waits)
            .GroupBy(wait => wait.WaitType, StringComparer.Ordinal)
            .Select(group => new PlanWait(group.Key, group.Sum(wait => wait.WaitTimeMs), group.Sum(wait => wait.WaitCount)))
            .OrderByDescending(wait => wait.WaitTimeMs).ThenBy(wait => wait.WaitType, StringComparer.Ordinal)
            .ToArray();
        var postgres = parts.Select(part => part.Postgres).OfType<PostgresBufferCounters>().ToArray();
        return new PlanMetrics(
            SumOrNull(parts.Select(part => part.GrantRequestedKb)),
            SumOrNull(parts.Select(part => part.GrantGrantedKb)),
            SumOrNull(parts.Select(part => part.GrantMaxUsedKb)),
            parts.Max(part => part.Dop),
            SumOrNull(parts.Select(part => part.CompileTimeMs)),
            SumOrNull(parts.Select(part => part.CompileCpuMs)),
            parts.Sum(part => part.SpillCount),
            parts.Any(part => part.HasWarnings),
            parts.Any(part => part.HasImplicitConversion),
            parts.Sum(part => part.MissingIndexCount),
            waits,
            postgres.Length == 0 ? null : new PostgresBufferCounters(
                postgres.Sum(p => p.SharedHit), postgres.Sum(p => p.SharedRead), postgres.Sum(p => p.SharedDirtied),
                postgres.Sum(p => p.SharedWritten), postgres.Sum(p => p.TempRead), postgres.Sum(p => p.TempWritten)));
    }

    internal static PlanMetrics Extract(string document)
    {
        if (string.IsNullOrWhiteSpace(document) || document.Length > MaximumCharacters)
            return PlanMetrics.Empty;
        try
        {
            var trimmed = document.AsSpan().TrimStart();
            return trimmed[0] is '{' or '[' ? ExtractExplain(document) : ExtractShowplan(document);
        }
        catch (Exception exception) when (exception is XmlException or JsonException or FormatException
            or OverflowException or InvalidOperationException or KeyNotFoundException)
        {
            return PlanMetrics.Empty;
        }
    }

    private static PlanMetrics ExtractShowplan(string document)
    {
        using var reader = XmlReader.Create(new StringReader(document), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters,
        });
        var root = XDocument.Load(reader);
        var elements = root.Descendants().ToArray();
        XElement[] Named(string localName) => elements.Where(element => element.Name.LocalName == localName).ToArray();

        var queryPlans = Named("QueryPlan");
        var grants = Named("MemoryGrantInfo");
        var waits = Named("Wait")
            .Where(wait => wait.Parent?.Name.LocalName == "WaitStats")
            .GroupBy(wait => Attribute(wait, "WaitType") ?? "UNKNOWN", StringComparer.Ordinal)
            .Select(group => new PlanWait(
                group.Key,
                group.Sum(wait => Long(wait, "WaitTimeMs") ?? 0),
                group.Sum(wait => Long(wait, "WaitCount") ?? 0)))
            .OrderByDescending(wait => wait.WaitTimeMs).ThenBy(wait => wait.WaitType, StringComparer.Ordinal)
            .ToArray();
        return new PlanMetrics(
            SumOrNull(grants.Select(grant => Long(grant, "RequestedMemory"))),
            SumOrNull(grants.Select(grant => Long(grant, "GrantedMemory"))),
            SumOrNull(grants.Select(grant => Long(grant, "MaxUsedMemory"))),
            queryPlans.Select(plan => (int?)Long(plan, "DegreeOfParallelism")).Max(),
            SumOrNull(queryPlans.Select(plan => Long(plan, "CompileTime"))),
            SumOrNull(queryPlans.Select(plan => Long(plan, "CompileCPU"))),
            Named("SpillToTempDb").Length,
            Named("Warnings").Length > 0,
            Named("PlanAffectingConvert").Length > 0
                || elements.Any(element => element.Attributes().Any(attribute =>
                    attribute.Value.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase))),
            Named("MissingIndexGroup").Length,
            waits,
            null);
    }

    private static PlanMetrics ExtractExplain(string document)
    {
        using var json = JsonDocument.Parse(document, new JsonDocumentOptions { MaxDepth = 256 });
        var root = json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0
            ? json.RootElement[0]
            : json.RootElement;
        if (!root.TryGetProperty("Plan", out var plan))
            return PlanMetrics.Empty;

        var spills = 0;
        var workers = 0;
        Walk(plan);
        void Walk(JsonElement node)
        {
            if (Text(node, "Sort Space Type") == "Disk")
                spills++;
            if (Number(node, "Hash Batches") > 1)
                spills++;
            workers = Math.Max(workers, (int)Number(node, "Workers Launched"));
            if (node.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                    Walk(child);
            }
        }

        return new PlanMetrics(
            null, null, null,
            workers > 0 ? workers + 1 : null,
            null, null,
            spills,
            plan.TryGetProperty("Warnings", out _),
            false,
            0,
            [],
            new PostgresBufferCounters(
                Number(plan, "Shared Hit Blocks"), Number(plan, "Shared Read Blocks"),
                Number(plan, "Shared Dirtied Blocks"), Number(plan, "Shared Written Blocks"),
                Number(plan, "Temp Read Blocks"), Number(plan, "Temp Written Blocks")));
    }

    private static long? SumOrNull(IEnumerable<long?> values)
    {
        long? sum = null;
        foreach (var value in values)
        {
            if (value is { } number)
                sum = (sum ?? 0) + number;
        }

        return sum;
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    private static long? Long(XElement element, string name) =>
        Attribute(element, name) is { } value && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;
}