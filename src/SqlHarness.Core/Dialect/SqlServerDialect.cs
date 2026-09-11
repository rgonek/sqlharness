namespace SqlHarness.Core.Dialect;

internal sealed class SqlServerDialect : ISqlDialect
{
    private static readonly IReadOnlySet<string> NoSessionTemps =
        new HashSet<string>(StringComparer.Ordinal);

    private readonly SqlSafetyClassifier _classifier = new();

    public SqlEngine Engine => SqlEngine.SqlServer;
    public string IdentitySql => SqlExecution.IdentitySql;
    public string PingSql => PingQuery.Sql;

    public SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables) =>
        // Local #temp is syntactic on SQL Server; sessionTempTables is ignored.
        _classifier.Classify(sql, usage, database, allowMutation, confirmDatabase);

    public IReadOnlySet<string> CollectSessionTempTables(string sql) => NoSessionTemps;

    public IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs) =>
        SqlParameterParser.Parse(inputs);

    public void ValidateMeasuredBatch(string sql)
    {
    }

    public DistilledPlan DistillPlan(string document) => PlanDistiller.Distill(document);

    public string CountsCatalogSql => CountsQuery.CatalogSql;

    public string BuildCountsExactSql(IReadOnlyList<ResolvedCountObject> objects) =>
        CountsQuery.BuildExactSql(objects);

    public string SchemaSql => SchemaReader.Sql;

    public async Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct)
    {
        const string enable = "SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;";
        const string disable = "SET STATISTICS XML OFF; SET STATISTICS TIME OFF; SET STATISTICS IO OFF;";
        Exception? primaryException = null;
        var messageStart = -1;
        var messagesCaptured = false;
        try
        {
            await BenchmarkCollector.ExecuteAndDrainAsync(session, new SqlExecutionCommand(enable, [], timeoutSeconds), ct);
            messageStart = session.Messages.Count;
            await using var reader = await session.ExecuteReaderAsync(new SqlExecutionCommand(sql, parameters, timeoutSeconds), ct);
            var result = await BenchmarkCollector.CollectCompareAsync(reader, raw, captureComparison, comparisonMaximumRows, ct);
            var messages = session.Messages.Skip(messageStart).ToArray();
            foreach (var message in messages)
                raw.AddMessage("sql", message);
            messagesCaptured = true;
            var io = StatisticsIoParser.Parse(string.Join(Environment.NewLine, messages));
            var time = StatisticsTimeParser.Parse(string.Join(Environment.NewLine, messages));
            var plans = result.PlanXmls.Select(ExecutionPlanParser.Parse).ToArray();
            var artifact = new CompareRunArtifact(
                variant,
                repetition,
                time.CpuTimeMs,
                time.ElapsedTimeMs,
                io.LogicalReads,
                io.Tables,
                result.Canonical.Hash,
                result.PlanXmls,
                messages.Length);
            return new CollectedCompareRun(artifact, plans, result.Comparison);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (messageStart >= 0 && !messagesCaptured)
            {
                try
                {
                    BenchmarkCollector.AppendMessages(session, messageStart, raw);
                }
                catch
                {
                    // Preserve the primary benchmark failure if message snapshotting also fails.
                }
            }
            throw;
        }
        finally
        {
            using var cleanupCts = new CancellationTokenSource(BenchmarkCollector.StatisticsCleanupTimeout);
            try
            {
                await BenchmarkCollector.ExecuteAndDrainAsync(
                        session,
                        new SqlExecutionCommand(disable, [], timeoutSeconds),
                        cleanupCts.Token)
                    .WaitAsync(BenchmarkCollector.StatisticsCleanupTimeout, CancellationToken.None);
            }
            catch when (primaryException is not null)
            {
                // Preserve the benchmark failure while still bounding the best-effort cleanup.
            }
        }
    }
}
