using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.WorkflowHost;

/// <summary>
/// Scenario-aware Core module: synthetic compare/matrix outcomes, production
/// artifact/plan reads, and an immediate failure on any database connect.
/// </summary>
public sealed class WorkflowModule : ISqlHarnessModule
{
    public const string UnknownSqlMessage =
        "Unknown scenario SQL; the fixture host does not fabricate outcomes.";
    public const string ConnectionForbiddenMessage =
        "Database connections are forbidden in the plan-077 fixture host.";

    private readonly WorkflowScenario _scenario;
    private readonly ArtifactOwner _owner;
    private readonly CompareArtifactWriter _writer;
    private readonly SqlHarnessModule _offline;
    private readonly ForbiddenSessionFactory _sessions;
    private readonly object _gate = new();
    private string? _lastArtifactDirectory;

    public WorkflowModule(WorkflowScenario scenario, ArtifactOwner owner, string compareRoot)
    {
        _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _sessions = new ForbiddenSessionFactory(this);
        _writer = new CompareArtifactWriter(compareRoot, () => DateTimeOffset.UnixEpoch);
        _offline = new SqlHarnessModule(_sessions, _writer, () => WorkflowScenarios.Profiles());
    }

    public int ConnectionAttempts { get; private set; }

    public async Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation switch
        {
            SqlHarnessArtifactReadOperation or SqlHarnessPlanOperation => await _offline.ExecuteAsync(operation, ct),
            SqlHarnessCompareOperation compare => ExecuteCompare(compare),
            SqlHarnessCompareMatrixOperation matrix => ExecuteMatrix(matrix),
            SqlHarnessMeasureOperation measure => ExecuteStudyMeasure(measure),
            SqlHarnessQueryOperation query => ExecuteStudyQuery(query),
            _ => RejectDatabaseFacing(operation),
        };
    }

    private SqlHarnessOutcome ExecuteStudyMeasure(SqlHarnessMeasureOperation measure)
    {
        if (_scenario.Id is not (WorkflowScenarios.ArtifactReviewStudyId or WorkflowScenarios.ArtifactReviewStudyV2Id)
            || !Matches(WorkflowScenarios.ArtifactReviewBaselineSql, measure.QuerySql)
            || !Matches(WorkflowScenarios.ArtifactReviewSetupSql, measure.SetupSql ?? string.Empty)
            || measure.ParameterSets is not { Count: 4 })
            return UnknownSql();

        var names = new[] { "small", "medium", "large", "edge" };
        if (!measure.ParameterSets.Select(set => set.Name).SequenceEqual(names, StringComparer.Ordinal))
            return UnknownSql();

        var tableNames = new[] { "Orders", "Customers", "Payments", "Returns" };
        var setReports = new List<MeasureParameterSetReport>(names.Length);
        var runs = new List<CompareRunArtifact>(names.Length * measure.Repeat);
        for (var setIndex = 0; setIndex < names.Length; setIndex++)
        {
            var name = names[setIndex];
            var reads = (setIndex + 1) * 100L;
            var variant = new CompareVariantReport(name,
                new CompareDistribution(4 + setIndex, 5 + setIndex, 6 + setIndex),
                new CompareDistribution(8 + setIndex, 9 + setIndex, 10 + setIndex),
                new CompareDistribution(reads, reads, reads),
                new Dictionary<string, long> { [tableNames[setIndex]] = reads }, [], [])
            {
                LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
                {
                    [tableNames[setIndex]] = new(reads, reads, reads),
                },
            };
            setReports.Add(new MeasureParameterSetReport(name,
                [new MeasureParameterMetadata("Batch", "int")],
                "fixture-value-hash-" + name, measure.Repeat, true, "fixture-result-hash-" + name,
                variant, []));
            for (var repetition = 1; repetition <= measure.Repeat; repetition++)
                runs.Add(new CompareRunArtifact("measure", repetition, 5 + setIndex, 9 + setIndex, reads,
                    new Dictionary<string, long>(StringComparer.Ordinal) { [tableNames[setIndex]] = reads },
                    "fixture-result-hash-" + name,
                    [PlanXml(WorkflowScenarios.ArtifactReviewBaselineSql, false, false)], 0, name)
                {
                    ExecutedSql = WorkflowScenarios.ArtifactReviewBaselineSql,
                });
        }

        var crossSet = new MeasureCrossSetSummary("small", 9, "edge", 12, "small", 5, "edge", 8,
            "small", 100, "edge", 400);
        var planStatementCount = StatementMetricsExtractor.CountStatements(PlanXml(WorkflowScenarios.ArtifactReviewBaselineSql, false, false));
        var projectedStatements = ArtifactStatementProjector.Project(ArtifactReader.MeasureSetKind, runs);
        if (projectedStatements.Statements.Count != 48)
        {
            var message = $"The offline measure fixture generated {projectedStatements.Statements.Count} statement records (plan count={planStatementCount}, run count={runs.Count}, first run SQL length={runs[0].ExecutedSql?.Length}, first run plan count={runs[0].PlanXmls.Count}); expected 48.";
            return new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, message,
                Error: SqlHarnessError.From(SqlHarnessExitCode.Safety, message));
        }
        var report = new SqlHarnessMeasureSetReport(Target(), measure.Repeat, runs.Count, measure.ParameterSets.Count, names,
            MeasureParameterSetReportProjector.MeasuredOrderRule,
            MeasureParameterSetReportProjector.PlanCacheWarning, setReports, crossSet, null);
        var directory = _writer.Write(report, runs, _scenario.ProfileName, _owner);
        _lastArtifactDirectory = directory;
        return Success(report with { ArtifactDirectory = directory }, runs);
    }

    private SqlHarnessOutcome ExecuteStudyQuery(SqlHarnessQueryOperation query)
    {
        if (_scenario.Id is not (WorkflowScenarios.ArtifactReviewStudyId or WorkflowScenarios.ArtifactReviewStudyV2Id)
            || !Matches(WorkflowScenarios.ArtifactReviewQuerySql, query.Sql))
            return UnknownSql();

        var largeRows = Enumerable.Range(0, 96)
            .Select(index => (IReadOnlyList<object?>)[index, "row-" + index.ToString("D3") + "-" + new string('x', 96)])
            .ToArray();
        var resultSets = new SqlHarnessResultSetReport[]
        {
            new([new(0, "id", "int", false), new(1, "payload", "nvarchar", false)], largeRows, 96, 0),
            new([new(0, "assertion", "nvarchar", false), new(1, "passed", "bit", false)],
                [["assertion: totals", true], ["assertion: candidate rows", true]], 2, 0),
            new([new(0, "assertion", "nvarchar", false), new(1, "value", "int", false)],
                [["assertion: final count", 4]], 1, 0),
        };
        return Success(new SqlHarnessQueryReport(Target(), "read-only", resultSets, [], -1, 4,
            "fixture-query-result-hash", new OutputFootprint(0, 96)));
    }

    private SqlHarnessOutcome ExecuteCompare(SqlHarnessCompareOperation compare)
    {
        if (compare.DryRun)
            return DryRun("compare");
        if (!Matches(_scenario.BaselineSql, compare.BaselineSql) || !Matches(_scenario.CandidateSql, compare.CandidateSql))
            return UnknownSql();
        if (_scenario.MatrixValues is { Count: > 0 })
            return UnknownSql();
        var shape = ShapeFor(null);
        return Persist(BuildPairReport(shape), shape);
    }

    private SqlHarnessOutcome ExecuteMatrix(SqlHarnessCompareMatrixOperation matrix)
    {
        if (matrix.DryRun)
            return DryRun("compare");
        if (!Matches(_scenario.BaselineSql, matrix.BaselineSql) || !Matches(_scenario.CandidateSql, matrix.CandidateSql))
            return UnknownSql();
        if (_scenario.MatrixValues is null || _scenario.MatrixName is null)
            return UnknownSql();

        var cells = new List<CompareMatrixCellReport>(_scenario.MatrixValues.Count);
        for (var index = 0; index < _scenario.MatrixValues.Count; index++)
        {
            var value = _scenario.MatrixValues[index];
            var shape = ShapeFor(index);
            var report = BuildPairReport(shape);
            var directory = Write(report, shape);
            cells.Add(new CompareMatrixCellReport(index, value, report with { ArtifactDirectory = directory }));
        }

        var coverage = MatrixExecutionCoverage.AllCompleted(cells.Count);
        _writer.WriteMatrixIndex(cells, coverage);
        _lastArtifactDirectory = cells[0].Compare.ArtifactDirectory;
        return Success(new SqlHarnessCompareMatrixReport(_scenario.MatrixName, _scenario.MatrixType, cells)
        {
            Coverage = coverage,
        }) with
        { Coverage = coverage };
    }

    private SqlHarnessOutcome Persist(SqlHarnessCompareReport report, PairShape shape)
    {
        var runs = BuildRuns(report, shape);
        var directory = _writer.Write(report, runs, _scenario.ProfileName, _owner);
        _lastArtifactDirectory = directory;
        return Success(report with { ArtifactDirectory = directory }, runs);
    }

    private string Write(SqlHarnessCompareReport report, PairShape shape)
    {
        var runs = BuildRuns(report, shape);
        return _writer.Write(report, runs, _scenario.ProfileName, _owner);
    }

    private readonly record struct PairShape(
        bool Equivalent,
        decimal BaselineElapsed,
        decimal CandidateElapsed,
        long BaselineReads,
        long CandidateReads,
        bool UnstableBaseline,
        bool WarningOnCandidate);

    private PairShape ShapeFor(int? matrixIndex)
    {
        if (_scenario.Id == WorkflowScenarios.PlanPagingId)
            return new(true, 40m, 12m, 12, 4, false, false);
        if (_scenario.Id is WorkflowScenarios.NoImprovementId or WorkflowScenarios.RequestScopeId)
            return new(false, 40m, 12m, 12, 4, false, false);
        if (_scenario.Id == WorkflowScenarios.UnavailableMetricsId)
            return new(true, 0.8m, 0.4m, 0, 0, false, false);
        if (matrixIndex == 7)
            return new(true, 40m, 80m, 12, 4, false, false);
        if (matrixIndex == 15)
            return new(true, 40m, 12m, 12, 4, true, false);
        return new(true, 40m, 12m, 12, 4, false, _scenario.Id == WorkflowScenarios.CollidingOperatorsId);
    }

    private SqlHarnessCompareReport BuildPairReport(PairShape shape)
    {
        var postgres = _scenario.Id == WorkflowScenarios.UnavailableMetricsId;
        var baselineElapsed = ElapsedSamples(shape.BaselineElapsed, shape.UnstableBaseline);
        var candidateElapsed = ElapsedSamples(shape.CandidateElapsed, false);
        var baseline = postgres
            ? PostgresVariant("baseline", 0, shape.BaselineElapsed)
            : Variant("baseline", shape.BaselineElapsed, shape.BaselineReads, baselineElapsed, warning: false);
        var candidate = postgres
            ? PostgresVariant("candidate", 0, shape.CandidateElapsed)
            : Variant("candidate", shape.CandidateElapsed, shape.CandidateReads, candidateElapsed, shape.WarningOnCandidate);

        var equivalence = new ResultEquivalenceReport(
            ResultComparisonMode.Ordered,
            shape.Equivalent,
            shape.Equivalent ? 0 : 3,
            shape.Equivalent ? 0 : 1,
            shape.Equivalent ? 0 : 1,
            MismatchReason: shape.Equivalent ? null : ResultMismatchReasons.Rows,
            BaselineStable: true,
            CandidateStable: true,
            SchemaEquivalent: true,
            MismatchReasons: shape.Equivalent ? [] : [ResultMismatchReasons.Rows]);
        var regression = RegressionPolicy.Evaluate(new RegressionInput(
            postgres ? SqlEngine.Postgres : SqlEngine.SqlServer,
            new RegressionVariantInput(
                baselineElapsed,
                ReadSamples(shape.BaselineReads),
                postgres ? [0L, 0, 0, 0, 0] : WholeSamples(baselineElapsed),
                BenchmarkMetricReport.Measured,
                postgres ? BenchmarkMetricReport.Unavailable : BenchmarkMetricReport.Measured,
                postgres ? BenchmarkMetricReport.Unavailable : BenchmarkMetricReport.Measured),
            new RegressionVariantInput(
                candidateElapsed,
                ReadSamples(shape.CandidateReads),
                postgres ? [0L, 0, 0, 0, 0] : WholeSamples(candidateElapsed),
                BenchmarkMetricReport.Measured,
                postgres ? BenchmarkMetricReport.Unavailable : BenchmarkMetricReport.Measured,
                postgres ? BenchmarkMetricReport.Unavailable : BenchmarkMetricReport.Measured),
            equivalence));

        return new SqlHarnessCompareReport(
            Target(), 5, 10, shape.Equivalent, baseline, candidate, null)
        {
            Equivalence = equivalence,
            Regression = regression,
            Parameters = Parameters(),
        };
    }

    private IReadOnlyList<BenchmarkParameterReport> Parameters() =>
        _scenario.Id switch
        {
            WorkflowScenarios.NoImprovementId or WorkflowScenarios.RequestScopeId =>
                [new BenchmarkParameterReport("Region", "nvarchar", 32, null, null)],
            WorkflowScenarios.PartialMatrixId =>
                [new BenchmarkParameterReport("BatchSize", "int", null, null, null)],
            _ => [],
        };

    private static IReadOnlyList<decimal> ElapsedSamples(decimal median, bool unstable) =>
        unstable
            ? [median - 20m, median - 10m, median, median + 10m, median + 30m]
            : [median, median, median, median, median];

    private static IReadOnlyList<long> ReadSamples(long reads) => [reads, reads, reads, reads, reads];

    private static IReadOnlyList<long> WholeSamples(IReadOnlyList<decimal> elapsed) =>
        elapsed.Select(sample => (long)decimal.Round(sample, MidpointRounding.AwayFromZero)).ToArray();

    private IReadOnlyList<CompareRunArtifact> BuildRuns(SqlHarnessCompareReport report, PairShape shape)
    {
        var planPagingFixture = _scenario.Id == WorkflowScenarios.PlanPagingId;
        var diagnosticFixture = _scenario.Id == WorkflowScenarios.ArtifactDiagnosticEvidenceId;
        var baselinePlan = PlanXml(_scenario.BaselineSql, warningOnSecond: false, planPagingFixture, diagnosticFixture);
        var candidatePlan = PlanXml(_scenario.CandidateSql, warningOnSecond: shape.WarningOnCandidate, planPagingFixture, diagnosticFixture);
        var baselineElapsed = ElapsedSamples(shape.BaselineElapsed, shape.UnstableBaseline);
        var candidateElapsed = ElapsedSamples(shape.CandidateElapsed, false);
        var table = _scenario.Id == WorkflowScenarios.CollidingOperatorsId ? "Accounts" : "Orders";
        var runs = new List<CompareRunArtifact>(10);
        for (var index = 0; index < 5; index++)
        {
            runs.Add(Run("baseline", index + 1, baselineElapsed[index], shape.BaselineReads, table, baselinePlan, report.Baseline.MetricReport)
                with
            { ExecutedSql = _scenario.BaselineSql });
            runs.Add(Run("candidate", index + 1, candidateElapsed[index], shape.CandidateReads, table, candidatePlan, report.Candidate.MetricReport)
                with
            {
                ExecutedSql = _scenario.CandidateSql,
                TableIo = _scenario.Id == WorkflowScenarios.ArtifactDiagnosticEvidenceId
                    ? [
                        new TableIoCounters("Orders", 5, 500, 0, 0, 0, 0, 0, 0),
                        new TableIoCounters("Customers", 7, 700, 0, 0, 0, 0, 0, 0),
                        new TableIoCounters("Payments", 8, 800, 0, 0, 0, 0, 0, 0),
                    ]
                    : _scenario.Id == WorkflowScenarios.ArtifactReviewStudyId && index == 0
                        ? [new TableIoCounters("Orders", 1, 999, 0, 0, 0, 0, 0, 0)]
                    : [],
                TableIoComplete = _scenario.Id == WorkflowScenarios.ArtifactDiagnosticEvidenceId,
            });
        }

        return runs;
    }

    private static CompareRunArtifact Run(
        string variant, int repetition, decimal elapsed, long reads, string table, string plan, BenchmarkMetricReport? metrics)
    {
        var whole = (long)decimal.Round(elapsed, MidpointRounding.AwayFromZero);
        var cpu = metrics?.CpuTimeAvailability == BenchmarkMetricReport.Unavailable ? 0 : whole;
        return new(
            variant, repetition, cpu, whole, reads,
            reads == 0 ? new Dictionary<string, long>() : new Dictionary<string, long> { [table] = reads },
            "hash-" + variant, [plan], 0,
            Metrics: metrics is null ? null : new BenchmarkRunMetrics(
                metrics.CpuTimeAvailability,
                metrics.ElapsedTimeAvailability,
                elapsed,
                metrics.ElapsedWholeMillisecondsAreExact,
                null,
                elapsed,
                metrics.LogicalReadsAvailability,
                metrics.LogicalReadsSource,
                metrics.RelationBufferSource,
                metrics.RelationBuffersAreAdditive,
                metrics.ResultRowSource,
                metrics.ResultRowSourceDescription,
                metrics.Warnings));
    }

    private CompareVariantReport Variant(
        string name, decimal elapsedMedian, long reads, IReadOnlyList<decimal> elapsedSamples, bool warning)
    {
        var whole = elapsedSamples.Select(sample => (long)decimal.Round(sample, MidpointRounding.AwayFromZero)).ToArray();
        var table = _scenario.Id == WorkflowScenarios.CollidingOperatorsId ? "Accounts" : "Orders";
        return new(
            name,
            new CompareDistribution(whole.Min(), Median(whole), whole.Max()),
            new CompareDistribution(whole.Min(), Median(whole), whole.Max()),
            new CompareDistribution(reads, reads, reads),
            reads == 0 ? new Dictionary<string, long>() : new Dictionary<string, long> { [table] = reads },
            Operators(warning),
            warning ? ["SpillToTempDb"] : [])
        {
            LogicalReadsByTable = reads == 0
                ? new Dictionary<string, CompareDistribution>()
                : new Dictionary<string, CompareDistribution> { [table] = new(reads, reads, reads) },
        };
    }

    private IReadOnlyList<CompareOperatorReport> Operators(bool warning) =>
        _scenario.Id == WorkflowScenarios.CollidingOperatorsId
            ?
            [
                new CompareOperatorReport(1, "Index Seek", "[dbo].[Accounts]", false, false, false),
                new CompareOperatorReport(1, "Clustered Index Scan", "[dbo].[Accounts]", warning, warning, false),
            ]
            :
            [
                new CompareOperatorReport(1, "Index Seek", "[dbo].[Orders]", false, false, false),
            ];

    private static long Median(long[] values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    private static CompareVariantReport PostgresVariant(string name, long cpu, decimal elapsed) => new(
        name,
        new CompareDistribution(0, 0, 0),
        new CompareDistribution(1, 1, 1),
        new CompareDistribution(0, 0, 0),
        new Dictionary<string, long>(),
        [new CompareOperatorReport(0, "Seq Scan", "public.orders", false, false, false)],
        [BenchmarkMetricText.PostgresCpuUnavailable, BenchmarkMetricText.PostgresSubMillisecond])
    {
        MetricReport = new BenchmarkMetricReport(
            BenchmarkMetricReport.Unavailable,
            BenchmarkMetricReport.Measured,
            new FractionalMilliseconds(elapsed, elapsed, elapsed),
            false,
            null,
            new FractionalMilliseconds(elapsed, elapsed, elapsed),
            BenchmarkMetricReport.Unavailable,
            BenchmarkMetricText.PostgresLogicalReadsSource,
            BenchmarkMetricText.PostgresRelationBufferSource,
            false,
            BenchmarkMetricReport.ResultStatement,
            BenchmarkMetricText.StatementRows,
            [BenchmarkMetricText.PostgresCpuUnavailable, BenchmarkMetricText.PostgresSubMillisecond]),
    };

    private SqlHarnessTargetIdentityReport Target()
    {
        var profile = WorkflowScenarios.Profiles()[_scenario.ProfileName];
        return new SqlHarnessTargetIdentityReport(
            profile.Server, profile.Database, profile.Server, profile.Database, "profile",
            Engine: _scenario.Engine);
    }

    private static string PlanXml(string sql, bool warningOnSecond, bool planPagingFixture, bool diagnosticFixture = false)
    {
        if (planPagingFixture)
        {
            var children = string.Concat(Enumerable.Range(1, 512).Select(index =>
                $"<RelOp NodeId=\"{index + 1}\" PhysicalOp=\"Index Seek\" LogicalOp=\"Index Seek\" EstimateRows=\"7\" EstimatedTotalSubtreeCost=\"0.1\"><IndexScan><Object Table=\"[dbo].[Orders]\" Index=\"[IX_Orders]\" /></IndexScan></RelOp>"));
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?><ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements><StmtSimple StatementText=\"SELECT Id FROM dbo.Orders;\"><QueryPlan><RelOp NodeId=\"1\" PhysicalOp=\"Nested Loops\" LogicalOp=\"Inner Join\" EstimateRows=\"512\" EstimatedTotalSubtreeCost=\"1\"><NestedLoops>" + children + "</NestedLoops></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>";
        }

        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (statements.Length == 0)
            statements = [sql];
        var body = string.Join("", statements.Select((text, index) => RelOp(text, warningOnSecond && index == 1, index, diagnosticFixture)));
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence>
                <Batch>
                  <Statements>
            """ + body + """
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;
    }

    private static string RelOp(string statementText, bool warning, int statementOrdinal, bool diagnosticFixture)
    {
        var escaped = System.Security.SecurityElement.Escape(statementText) ?? statementText;
        var warnings = warning
            ? "<Warnings><SpillToTempDb SpillLevel=\"1\" /></Warnings>"
            : "";
        var clustered = statementText.Contains("COUNT_BIG", StringComparison.OrdinalIgnoreCase);
        var accounts = statementText.Contains("dbo.Accounts", StringComparison.OrdinalIgnoreCase);
        var table = statementText.Contains("dbo.Customers", StringComparison.OrdinalIgnoreCase) ? "[dbo].[Customers]"
            : statementText.Contains("dbo.Payments", StringComparison.OrdinalIgnoreCase) ? "[dbo].[Payments]"
            : accounts ? "[dbo].[Accounts]" : "[dbo].[Orders]";
        var physical = clustered ? "Clustered Index Scan" : "Index Seek";
        var index = clustered ? "[PK_Accounts]" : accounts ? "[IX_Accounts]"
            : table.Contains("Customers", StringComparison.Ordinal) ? "[IX_Customers]"
            : table.Contains("Payments", StringComparison.Ordinal) ? "[IX_Payments]" : "[IX_Orders]";
        var queryPlanAttributes = diagnosticFixture
            ? $" DegreeOfParallelism=\"{1 + statementOrdinal}\" CompileTime=\"{20 - statementOrdinal * 3}\" CompileCPU=\"{10 + statementOrdinal}\""
            : string.Empty;
        var memoryGrant = diagnosticFixture
            ? $"<MemoryGrantInfo RequestedMemory=\"{100 + statementOrdinal * 50}\" GrantedMemory=\"{80 + statementOrdinal * 40}\" MaxUsedMemory=\"{60 + statementOrdinal * 30}\" />"
            : string.Empty;
        var counters = diagnosticFixture
            ? $"<RunTimeInformation><RunTimeCountersPerThread Thread=\"0\" ActualRows=\"1\" ActualExecutions=\"1\" ActualLogicalReads=\"{100 + statementOrdinal * 10}\" ActualLobLogicalReads=\"{statementOrdinal * 2}\" ActualScans=\"{3 + statementOrdinal}\" /></RunTimeInformation>"
            : string.Empty;
        return $"""
                    <StmtSimple StatementText="{escaped}">
                      <QueryPlan{queryPlanAttributes}>
                        {memoryGrant}
                        <QueryTimeStats CpuTime="10" ElapsedTime="14" />
                        <RelOp NodeId="1" PhysicalOp="{physical}" LogicalOp="{physical}" EstimateRows="7" EstimatedTotalSubtreeCost="0.6">
                          {warnings}
                          {counters}
                          <IndexScan>
                            <Object Table="{table}" Index="{index}" />
                          </IndexScan>
                        </RelOp>
                      </QueryPlan>
                    </StmtSimple>
            """;
    }

    private static bool Matches(string expected, string actual) =>
        string.Equals(Normalize(expected), Normalize(actual), StringComparison.Ordinal);

    private static string Normalize(string sql) =>
        sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static SqlHarnessOutcome DryRun(string command) =>
        new(SqlHarnessExitCode.Success, new SqlHarnessDryRunReport(command, "read-only", "read-only", [], 0), null);

    private static SqlHarnessOutcome UnknownSql() =>
        new(SqlHarnessExitCode.Safety, null, UnknownSqlMessage, Error: SqlHarnessError.From(SqlHarnessExitCode.Safety, UnknownSqlMessage));

    private SqlHarnessOutcome RejectDatabaseFacing(SqlHarnessOperation operation)
    {
        if (operation is SqlHarnessQueryOperation query && query.DryRun)
            return DryRun("query");
        return new SqlHarnessOutcome(
            SqlHarnessExitCode.Safety,
            null,
            ConnectionForbiddenMessage,
            Error: SqlHarnessError.From(SqlHarnessExitCode.Safety, ConnectionForbiddenMessage));
    }

    private SqlHarnessOutcome Success(object report, IReadOnlyList<CompareRunArtifact>? runs = null) =>
        new(SqlHarnessExitCode.Success, report, null) { BenchmarkRuns = runs };

    internal void NoteConnectionAttempt()
    {
        lock (_gate)
            ConnectionAttempts++;
    }

    private sealed class ForbiddenSessionFactory(WorkflowModule module) : ISqlSessionFactory
    {
        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            module.NoteConnectionAttempt();
            throw new InvalidOperationException(ConnectionForbiddenMessage);
        }
    }
}