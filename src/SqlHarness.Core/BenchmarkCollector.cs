using System.Globalization;

namespace SqlHarness.Core;

internal static class BenchmarkCollector
{
    internal static readonly TimeSpan StatisticsCleanupTimeout = TimeSpan.FromSeconds(5);

    internal static readonly CanonicalComparisonResult EmptyComparison =
        new(string.Empty, Array.Empty<string>());

    internal static async Task<CollectedCompare> CollectCompareAsync(
        ISqlReader reader,
        CanonicalResultAccumulator raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct)
    {
        using var canonical = new CanonicalResultAccumulator();
        using var comparison = captureComparison
            ? new CanonicalComparisonAccumulator(comparisonMaximumRows)
            : null;
        var planXmls = new List<string>();
        do
        {
            if (reader.FieldCount == 0)
                continue;
            if (reader.FieldCount == 1 && reader.GetName(0).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase))
            {
                while (await reader.ReadAsync(ct))
                {
                    var planXml = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
                    planXmls.Add(planXml);
                    raw.AddMessage("planXml", planXml);
                }
                continue;
            }

            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(index => new CanonicalColumn(index, reader.GetName(index), reader.GetFieldType(index).FullName ?? reader.GetFieldType(index).Name, reader.GetAllowNull(index)))
                .ToArray();
            canonical.BeginResultSet(columns);
            comparison?.BeginResultSet(columns);
            raw.BeginResultSet(columns);
            while (await reader.ReadAsync(ct))
            {
                var values = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => NormalizeValue(reader.GetValue(index)))
                    .ToArray();
                canonical.AddRow(values);
                comparison?.AddRow(values);
                raw.AddRow(values);
            }
            canonical.EndResultSet();
            comparison?.EndResultSet();
            raw.EndResultSet();
        } while (await reader.NextResultAsync(ct));
        return new CollectedCompare(
            canonical.Complete(),
            comparison?.Complete() ?? EmptyComparison,
            planXmls);
    }

    internal static void AppendMessages(ISqlSession session, int messageStart, CanonicalResultAccumulator raw)
    {
        foreach (var message in session.Messages.Skip(messageStart))
            raw.AddMessage("sql", message);
    }

    internal static async Task ExecuteAndDrainAsync(
        ISqlSession session,
        SqlExecutionCommand command,
        CancellationToken ct)
    {
        await using var reader = await session.ExecuteReaderAsync(command, ct);
        do
        {
            while (await reader.ReadAsync(ct))
            {
            }
        } while (await reader.NextResultAsync(ct));
    }

    internal static object? NormalizeValue(object value) => value is DBNull ? null : value;
}