namespace SqlHarness.Core;

internal static class IndexInventoryCapture
{
    public const int MaxInventoryTables = 8;
    public const int MaxIndexesPerTable = 64;

    /// <summary>
    /// Distinct (schema, table) of hints whose database is null or equals the session database, in first-appearance order.
    /// A null schema is <c>dbo</c>.
    /// </summary>
    internal static IReadOnlyList<(string Schema, string Table)> HintedTables(
        IEnumerable<DistilledPlan> plans,
        string database,
        out int skippedTables,
        out int otherDatabaseHints)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(database);
        var seen = new HashSet<(string Schema, string Table)>(SchemaTableComparer.Instance);
        var ordered = new List<(string Schema, string Table)>();
        var other = 0;
        foreach (var plan in plans)
        {
            foreach (var statement in plan.Statements)
            {
                foreach (var hint in statement.MissingIndexes)
                {
                    if (hint.Database is not null &&
                        !string.Equals(hint.Database, database, StringComparison.OrdinalIgnoreCase))
                    {
                        other++;
                        continue;
                    }

                    // Task 4 matches a null schema to dbo, so the inventory row uses that schema.
                    var key = (hint.Schema ?? "dbo", hint.Table);
                    if (seen.Add(key))
                        ordered.Add(key);
                }
            }
        }

        otherDatabaseHints = other;
        skippedTables = Math.Max(0, ordered.Count - MaxInventoryTables);
        return ordered.Count <= MaxInventoryTables
            ? ordered
            : ordered.Take(MaxInventoryTables).ToArray();
    }

    /// <summary>
    /// Returns null when no plan has an in-session hint or the engine is not SQL Server.
    /// Never throws except <see cref="OperationCanceledException"/> when <paramref name="ct"/> is already cancelled.
    /// </summary>
    internal static async Task<IndexInventory?> CaptureAsync(
        SqlEngine engine,
        ISqlSession session,
        IReadOnlyList<string> planXmls,
        int timeoutSeconds,
        IReadOnlyList<string> knownSecrets,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(planXmls);
        ArgumentNullException.ThrowIfNull(knownSecrets);
        if (engine != SqlEngine.SqlServer)
            return null;

        var database = string.Empty;
        var commandStarted = false;
        var logicalBatchEnded = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            database = session.Identity.ActualDatabase;
            var distilled = new DistilledPlan[planXmls.Count];
            for (var index = 0; index < planXmls.Count; index++)
                distilled[index] = PlanDistiller.Distill(planXmls[index]);

            var tables = HintedTables(distilled, database, out var skippedTables, out _);
            if (tables.Count == 0)
                return null;

            var command = new SqlExecutionCommand(
                IndexInventoryQuery.Sql,
                IndexInventoryQuery.Parameters(tables),
                Math.Min(30, timeoutSeconds));
            commandStarted = true;
            IReadOnlyList<InventoryTable> inventoryTables;
            await using (var reader = await session.ExecuteReaderAsync(command, ct))
                inventoryTables = await IndexInventoryQuery.ReadAsync(reader, tables, ct);
            logicalBatchEnded = true;
            _ = await SqlSessionLogicalBatch.EndAsync(session, ct);
            IReadOnlyList<string> gaps = skippedTables > 0
                ? [$"Index inventory covers the first {MaxInventoryTables} hinted tables; {skippedTables} more were skipped."]
                : [];
            return new IndexInventory(database, DateTimeOffset.UtcNow, inventoryTables, gaps);
        }
        catch (SessionCleanupException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (commandStarted && !logicalBatchEnded)
            {
                logicalBatchEnded = true;
                _ = await SqlSessionLogicalBatch.EndAsync(session, CancellationToken.None);
            }
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (commandStarted && !logicalBatchEnded)
            {
                logicalBatchEnded = true;
                _ = await SqlSessionLogicalBatch.EndAsync(session, ct);
            }
            return new IndexInventory(
                database,
                DateTimeOffset.UtcNow,
                [],
                [$"Index inventory unavailable: {SecretRedactor.Redact(exception, knownSecrets)}."]);
        }
    }

    private sealed class SchemaTableComparer : IEqualityComparer<(string Schema, string Table)>
    {
        internal static SchemaTableComparer Instance { get; } = new();

        public bool Equals((string Schema, string Table) x, (string Schema, string Table) y) =>
            string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Table) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table));
    }
}
