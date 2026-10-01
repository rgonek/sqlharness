using System.Globalization;

namespace SqlHarness.Core.Dialect;

internal sealed class SqlServerDialect : ISqlDialect
{
    private const string StatisticsTruncatedWarning =
        "SQL Server informational messages exceeded the per-command limit and {0} messages were omitted. CPU time, elapsed time and logicalReads parsed from STATISTICS output are unavailable; logicalReads 0 is not a measured zero.";

    private readonly SqlSafetyClassifier _classifier = new();

    public SqlEngine Engine => SqlEngine.SqlServer;
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

    // Legacy text adapter kept for compatibility and characterization tests only (012/final F3): no production call site uses it.
    public IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs) =>
        BindParameters(inputs.Select(SqlParameterParser.ToInput));

    public IReadOnlyList<SqlHarnessParameter> BindParameters(IEnumerable<SqlHarnessParameterInput> inputs) =>
        SqlParameterParser.Bind(inputs);

    public void ValidateParameterReferences(IReadOnlyList<SqlHarnessParameter> parameters, params string?[] batches) =>
        SqlParameterReferenceValidator.Validate(parameters, batches);

    public void ValidateMeasuredBatch(string sql)
    {
    }

    public string CountsCatalogSql => CountsQuery.CatalogSql;

    public string BuildCountsExactSql(IReadOnlyList<ResolvedCountObject> objects) =>
        CountsQuery.BuildExactSql(objects);

    public string SchemaSql => SchemaReader.Sql;

    public string SpaceSql => SpaceQuery.Sql;

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
            // The control command carries no measured data: discard its diagnostics so
            // the measured window below owns the omitted-message attribution.
            _ = session.ConsumeMessages(0);
            messageStart = session.Messages.Count;
            await using var reader = await session.ExecuteReaderAsync(new SqlExecutionCommand(sql, parameters, timeoutSeconds), ct);
            var result = await BenchmarkCollector.CollectCompareAsync(reader, raw, captureComparison, comparisonMaximumRows, ct);
            // Per-command consumption: the session releases this run's messages instead
            // of retaining every repetition's notices for the whole session.
            var consumed = session.ConsumeMessages(messageStart);
            var messages = consumed.Messages.ToArray();
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
                messages.Length,
                Metrics: TruncatedMetricsOrNull(consumed.OmittedMessageCount));
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

            try
            {
                // Release the control command's diagnostics; never mask the run outcome.
                _ = session.ConsumeMessages(0);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Explicit incomplete metrics after the per-command message bound dropped
    /// arrivals: STATISTICS IO/TIME text may be partial, so parsed zeros are
    /// reported as unavailable instead of silently measured zeros.
    /// </summary>
    private static BenchmarkRunMetrics? TruncatedMetricsOrNull(int omittedMessageCount) =>
        omittedMessageCount <= 0
            ? null
            : new BenchmarkRunMetrics(
                BenchmarkMetricReport.Unavailable,
                BenchmarkMetricReport.Unavailable,
                null,
                false,
                null,
                null,
                BenchmarkMetricReport.Unavailable,
                null,
                null,
                null,
                BenchmarkMetricReport.ResultStatement,
                BenchmarkMetricText.StatementRows,
                [string.Format(
                    CultureInfo.InvariantCulture,
                    StatisticsTruncatedWarning,
                    omittedMessageCount)]);

}