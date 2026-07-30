namespace SqlHarness.Core;

internal sealed record CollectedQueryResult(
    IReadOnlyList<SqlHarnessResultSetReport> ResultSets,
    IReadOnlyList<string> Messages,
    int RecordsAffected,
    CanonicalResult Canonical);

internal static class QueryResultCollector
{
    internal static async Task<CollectedQueryResult> CollectAsync(
        ISqlSession session,
        SqlExecutionCommand command,
        int maxRows,
        IReadOnlyCollection<string> knownSecrets,
        CanonicalResultAccumulator raw,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(knownSecrets);
        ArgumentNullException.ThrowIfNull(raw);

        var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
        var messageStart = session.Messages.Count;
        await using var reader = await session.ExecuteReaderAsync(command, ct);

        var retained = 0;
        var reports = new List<SqlHarnessResultSetReport>();
        using var canonical = new CanonicalResultAccumulator();
        var rawResultSetOpen = false;
        try
        {
            do
            {
                if (reader.FieldCount == 0)
                    continue;

                var canonicalColumns = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => new CanonicalColumn(
                        index,
                        reader.GetName(index),
                        reader.GetFieldType(index).FullName ?? reader.GetFieldType(index).Name,
                        reader.GetAllowNull(index)))
                    .ToArray();
                var publicColumns = canonicalColumns
                    .Select(column => new SqlHarnessColumnReport(
                        column.Ordinal,
                        column.Name,
                        column.DataType,
                        column.AllowNull))
                    .ToArray();
                var rows = new List<IReadOnlyList<object?>>();
                long rowCount = 0;
                canonical.BeginResultSet(canonicalColumns);
                raw.BeginResultSet(canonicalColumns);
                rawResultSetOpen = true;
                while (await reader.ReadAsync(ct))
                {
                    var values = Enumerable.Range(0, reader.FieldCount)
                        .Select(index => NormalizeValue(reader.GetValue(index)))
                        .ToArray();
                    canonical.AddRow(values);
                    raw.AddRow(values);
                    rowCount++;
                    if (retained < maxRows)
                    {
                        rows.Add(values);
                        retained++;
                    }
                }
                canonical.EndResultSet();
                raw.EndResultSet();
                rawResultSetOpen = false;
                reports.Add(new SqlHarnessResultSetReport(
                    publicColumns,
                    rows,
                    rowCount,
                    rowCount - rows.Count));
            } while (await reader.NextResultAsync(ct));
        }
        catch
        {
            if (rawResultSetOpen)
                raw.EndResultSet();
            AppendSafeMessages(raw, session.Messages, messageStart, secrets);
            throw;
        }

        var safeMessages = SafeMessageSlice(session.Messages, messageStart, secrets);
        foreach (var message in safeMessages)
        {
            canonical.AddMessage("sql", message);
            raw.AddMessage("sql", message);
        }

        return new CollectedQueryResult(
            reports,
            safeMessages,
            reader.RecordsAffected,
            canonical.Complete());
    }

    private static void AppendSafeMessages(
        CanonicalResultAccumulator raw,
        IReadOnlyList<string> messages,
        int messageStart,
        IReadOnlyList<string> secrets)
    {
        foreach (var message in SafeMessageSlice(messages, messageStart, secrets))
            raw.AddMessage("sql", message);
    }

    private static IReadOnlyList<string> SafeMessageSlice(
        IReadOnlyList<string> messages,
        int messageStart,
        IReadOnlyList<string> secrets) =>
        messages.Skip(messageStart)
            .Select(message => SecretRedactor.Redact(message, secrets))
            .ToArray();

    private static object? NormalizeValue(object value) => value is DBNull ? null : value;
}
