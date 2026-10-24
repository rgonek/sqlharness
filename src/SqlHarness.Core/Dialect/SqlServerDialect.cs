using System.Globalization;

namespace SqlHarness.Core.Dialect;

internal sealed class SqlServerDialect : ISqlDialect
{
    private const string StatisticsTruncatedWarning =
        "SQL Server informational messages exceeded the per-command limit and {0} messages were omitted. CPU time and elapsed time parsed from STATISTICS output are unavailable; logicalReads is measured only when STATISTICS IO was completely parsed.";

    private const string StatisticsUnrecognizedWarning =
        "SQL Server STATISTICS TIME output was not recognized (the session language may not be English). CPU time and elapsed time are unavailable; logicalReads is measured only when STATISTICS IO was completely parsed.";

    private readonly SqlSafetyClassifier _classifier = new();

    public SqlEngine Engine => SqlEngine.SqlServer;

    private static readonly (string Name, int Bit)[] RestorableOptions =
    [
        ("NOCOUNT", 512),
        ("XACT_ABORT", 16384),
        ("ARITHABORT", 64),
        ("ANSI_NULLS", 32),
        ("ANSI_WARNINGS", 8),
        ("ANSI_PADDING", 16),
        ("QUOTED_IDENTIFIER", 256),
        ("CONCAT_NULL_YIELDS_NULL", 4096),
        ("NUMERIC_ROUNDABORT", 8192),
    ];

    public async Task<SessionState?> CaptureSessionStateAsync(ISqlSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        const string sql = "SELECT @@OPTIONS, transaction_isolation_level, @@LOCK_TIMEOUT, @@DATEFIRST, date_format, deadlock_priority FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
        var messageStart = session.Messages.Count;
        await using var reader = await session.ExecuteReaderAsync(new SqlExecutionCommand(sql, [], 30), ct);
        try
        {
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("SQL Server did not return the current session state.");

            return new SessionState(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture)
                    ?? throw new InvalidOperationException("SQL Server returned no DATEFORMAT state."),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture));
        }
        finally
        {
            _ = session.ConsumeMessages(messageStart);
        }
    }

    public async Task RestoreSessionStateAsync(ISqlSession session, SessionState? state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (state is null)
            return;

        var statements = RestorableOptions.Select(option =>
            $"SET {option.Name} {((state.Options & option.Bit) == 0 ? "OFF" : "ON")}");
        var isolation = state.IsolationLevel switch
        {
            1 => "READ UNCOMMITTED",
            2 => "READ COMMITTED",
            3 => "REPEATABLE READ",
            4 => "SERIALIZABLE",
            5 => "SNAPSHOT",
            _ => throw new InvalidOperationException($"SQL Server returned unsupported isolation level {state.IsolationLevel}."),
        };
        var dateFormat = state.DateFormat.ToLowerInvariant() switch
        {
            "mdy" => "mdy",
            "dmy" => "dmy",
            "ymd" => "ymd",
            "ydm" => "ydm",
            "myd" => "myd",
            "dym" => "dym",
            _ => throw new InvalidOperationException("SQL Server returned an unsupported DATEFORMAT state."),
        };
        statements = statements.Concat(
        [
            $"SET TRANSACTION ISOLATION LEVEL {isolation}",
            $"SET LOCK_TIMEOUT {state.LockTimeout.ToString(CultureInfo.InvariantCulture)}",
            $"SET DATEFIRST {state.DateFirst.ToString(CultureInfo.InvariantCulture)}",
            $"SET DATEFORMAT {dateFormat}",
            $"SET DEADLOCK_PRIORITY {state.DeadlockPriority.ToString(CultureInfo.InvariantCulture)}",
        ]);

        var messageStart = session.Messages.Count;
        try
        {
            await BenchmarkCollector.ExecuteAndDrainAsync(
                session,
                new SqlExecutionCommand(string.Join("; ", statements), [], 30),
                ct);
        }
        finally
        {
            _ = session.ConsumeMessages(messageStart);
        }
    }

    public SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables) =>
        // Local #temp is syntactic on SQL Server; sessionTempTables is ignored.
        _classifier.Classify(sql, usage, database, allowMutation, confirmDatabase);

    public SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables,
        IReadOnlySet<string> tableValuedParameterNames) =>
        _classifier.Classify(sql, usage, database, allowMutation, confirmDatabase, tableValuedParameterNames);

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

    public async Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator? raw,
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
                raw?.AddMessage("sql", message.Text);
            messagesCaptured = true;
            var ioMessages = messages.Where(message => message.Number == 3615).Select(message => message.Text).ToArray();
            var timeMessages = messages.Where(message => message.Number is 3612 or 3613).Select(message => message.Text).ToArray();
            var io = StatisticsIoParser.Parse(string.Join(Environment.NewLine, ioMessages));
            var time = StatisticsTimeParser.Parse(string.Join(Environment.NewLine, timeMessages));
            if (consumed.OmittedMessageCount > 0 && consumed.TableIoComplete)
            {
                var typedTables = new Dictionary<StatisticsIoTableIdentity, long>();
                foreach (var table in consumed.TableIo)
                {
                    var identity = table.Identity ?? StatisticsIoTableIdentity.Unknown(table.Table);
                    typedTables[identity] = typedTables.GetValueOrDefault(identity) + table.LogicalReads + table.LobLogicalReads;
                }
                var tables = consumed.RawTableLogicalReads is { } rawTables
                    ? rawTables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    : typedTables.ToDictionary(pair => pair.Key.DisplayName, pair => pair.Value, StringComparer.Ordinal);
                io = new StatisticsIo(tables.Values.Sum(), tables) { TypedTables = typedTables };
            }
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
                Metrics: UnavailableMetricsOrNull(consumed.OmittedMessageCount, time, consumed.TableIoComplete))
            {
                TableIo = consumed.TableIo,
                TableIoComplete = consumed.TableIoComplete,
                LogicalReadsByTableIdentity = consumed.TableIoComplete ? io.TypedTables : null,
            };
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
    /// Incomplete STATISTICS metrics. Omitted messages may have dropped IO/TIME
    /// text; zero recognized English TIME blocks means the output was not
    /// understood. Parsed zeros are unavailable, not measured zeros. Omitted
    /// messages take precedence over unrecognized text.
    /// </summary>
    private static BenchmarkRunMetrics? UnavailableMetricsOrNull(
        int omittedMessageCount,
        StatisticsTime time,
        bool tableIoComplete)
    {
        var timeAvailable = omittedMessageCount <= 0 && time.RecognizedBlocks > 0;
        var readsAvailable = tableIoComplete;
        if (timeAvailable && readsAvailable)
            return null;

        var warnings = new List<string>();
        if (omittedMessageCount > 0)
            warnings.Add(string.Format(CultureInfo.InvariantCulture, StatisticsTruncatedWarning, omittedMessageCount));
        else if (!timeAvailable)
            warnings.Add(StatisticsUnrecognizedWarning);
        if (!readsAvailable && timeAvailable)
            warnings.Add("SQL Server STATISTICS IO output was not completely parsed; logicalReads is unavailable.");

        return new BenchmarkRunMetrics(
            timeAvailable ? BenchmarkMetricReport.Measured : BenchmarkMetricReport.Unavailable,
            timeAvailable ? BenchmarkMetricReport.Measured : BenchmarkMetricReport.Unavailable,
            timeAvailable ? time.ElapsedTimeMs : null,
            timeAvailable,
            null,
            null,
            readsAvailable ? BenchmarkMetricReport.Measured : BenchmarkMetricReport.Unavailable,
            null,
            null,
            null,
            BenchmarkMetricReport.ResultStatement,
            BenchmarkMetricText.StatementRows,
            warnings);
    }

}
