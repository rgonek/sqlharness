namespace SqlHarness.Core;

internal sealed record CollectedQueryResult(
    IReadOnlyList<SqlHarnessResultSetReport> ResultSets,
    IReadOnlyList<string> Messages,
    int RecordsAffected,
    CanonicalResult Canonical,
    int OmittedMessageCount = 0);

internal static class QueryResultCollector
{
    /// <summary>
    /// Opens the reader, then invokes <paramref name="createRaw"/> so open failures leave
    /// the caller's raw accumulator unset (footprint remains (0, 0)).
    /// Reads every row of every result set on this execution's reader.
    /// <paramref name="maxRows"/> is a presentation-only retention cap (global across
    /// result sets): all rows are still read and hashed into the canonical and raw
    /// accumulators, so canonical hashes and result equivalence cover the complete
    /// result while only the first <paramref name="maxRows"/> rows are retained for
    /// display. No TOP/LIMIT is injected and reading never stops early.
    /// </summary>
    /// <remarks>
    /// Single-cell memory: each cell arrives via <c>ISqlReader.GetValue</c>, which
    /// fully materializes the value (a varchar(max)/varbinary(max) cell can hold
    /// megabytes) before hashing. The carved-out <see cref="ChunkedCanonicalCellHash"/>
    /// stage hashes such cells from bounded chunks with a byte-identical digest and
    /// is the designated path for any future chunked reader; constant-memory reads
    /// are not claimed until that wiring exists.
    /// </remarks>
    internal static async Task<CollectedQueryResult> CollectAsync(
        ISqlSession session,
        SqlExecutionCommand command,
        int maxRows,
        IReadOnlyCollection<string> knownSecrets,
        Func<CanonicalResultAccumulator> createRaw,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(knownSecrets);
        ArgumentNullException.ThrowIfNull(createRaw);

        var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
        var messageStart = session.Messages.Count;
        // Open first so ExecuteReaderAsync failures never construct raw framing bytes.
        await using var reader = await session.ExecuteReaderAsync(command, ct);
        var raw = createRaw() ?? throw new InvalidOperationException("createRaw returned null.");

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
            AppendSafeMessages(raw, session.ConsumeMessages(messageStart), secrets);
            throw;
        }

        // Per-command consumption: only this execution's messages are published and
        // the session releases them, so repetitions never accumulate whole history.
        var consumed = session.ConsumeMessages(messageStart);
        var safeMessages = RedactMessages(consumed.Messages, secrets);
        foreach (var message in safeMessages)
        {
            canonical.AddMessage("sql", message);
            raw.AddMessage("sql", message);
        }

        return new CollectedQueryResult(
            reports,
            safeMessages,
            reader.RecordsAffected,
            canonical.Complete(),
            consumed.OmittedMessageCount);
    }

    private static void AppendSafeMessages(
        CanonicalResultAccumulator raw,
        ConsumedSessionMessages consumed,
        IReadOnlyList<string> secrets)
    {
        foreach (var message in RedactMessages(consumed.Messages, secrets))
            raw.AddMessage("sql", message);
    }

    private static IReadOnlyList<string> RedactMessages(
        IReadOnlyList<string> messages,
        IReadOnlyList<string> secrets) =>
        messages.Select(message => SecretRedactor.Redact(message, secrets))
            .ToArray();

    private static object? NormalizeValue(object value) => value is DBNull ? null : value;
}