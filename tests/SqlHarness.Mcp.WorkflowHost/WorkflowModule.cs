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
            _ => RejectDatabaseFacing(operation),
        };
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

        _writer.WriteMatrixIndex(cells);
        _lastArtifactDirectory = cells[0].Compare.ArtifactDirectory;
        return Success(new SqlHarnessCompareMatrixReport(_scenario.MatrixName, _scenario.MatrixType, cells));
    }

    private SqlHarnessOutcome Persist(SqlHarnessCompareReport report, PairShape shape)
    {
        var directory = Write(report, shape);
        _lastArtifactDirectory = directory;
        return Success(report with { ArtifactDirectory = directory });
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
            MismatchReason: shape.Equivalent ? null : "rows");
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
            : median < 2m
                ? [median, median, median, median, median]
                : [median - 2m, median - 1m, median, median + 1m, median + 2m];

    private static IReadOnlyList<long> ReadSamples(long reads) => [reads, reads, reads, reads, reads];

    private static IReadOnlyList<long> WholeSamples(IReadOnlyList<decimal> elapsed) =>
        elapsed.Select(sample => (long)decimal.Round(sample, MidpointRounding.AwayFromZero)).ToArray();

    private IReadOnlyList<CompareRunArtifact> BuildRuns(SqlHarnessCompareReport report, PairShape shape)
    {
        var baselinePlan = PlanXml(_scenario.BaselineSql, warningOnSecond: false);
        var candidatePlan = PlanXml(_scenario.CandidateSql, warningOnSecond: shape.WarningOnCandidate);
        var baselineElapsed = ElapsedSamples(shape.BaselineElapsed, shape.UnstableBaseline);
        var candidateElapsed = ElapsedSamples(shape.CandidateElapsed, false);
        var table = _scenario.Id == WorkflowScenarios.CollidingOperatorsId ? "Accounts" : "Orders";
        var runs = new List<CompareRunArtifact>(10);
        for (var index = 0; index < 5; index++)
        {
            runs.Add(Run("baseline", index + 1, baselineElapsed[index], shape.BaselineReads, table, baselinePlan, report.Baseline.MetricReport));
            runs.Add(Run("candidate", index + 1, candidateElapsed[index], shape.CandidateReads, table, candidatePlan, report.Candidate.MetricReport));
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

    private static string PlanXml(string sql, bool warningOnSecond)
    {
        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (statements.Length == 0)
            statements = [sql];
        var body = string.Join("", statements.Select((text, index) => RelOp(text, warningOnSecond && index == 1)));
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

    private static string RelOp(string statementText, bool warning)
    {
        var escaped = System.Security.SecurityElement.Escape(statementText) ?? statementText;
        var warnings = warning
            ? "<Warnings><SpillToTempDb SpillLevel=\"1\" /></Warnings>"
            : "";
        var clustered = statementText.Contains("COUNT_BIG", StringComparison.OrdinalIgnoreCase);
        var accounts = statementText.Contains("dbo.Accounts", StringComparison.OrdinalIgnoreCase);
        var physical = clustered ? "Clustered Index Scan" : "Index Seek";
        var table = accounts ? "[dbo].[Accounts]" : "[dbo].[Orders]";
        var index = clustered ? "[PK_Accounts]" : accounts ? "[IX_Accounts]" : "[IX_Orders]";
        return $"""
                    <StmtSimple StatementText="{escaped}">
                      <QueryPlan>
                        <RelOp NodeId="1" PhysicalOp="{physical}" LogicalOp="{physical}" EstimateRows="7" EstimatedTotalSubtreeCost="0.6">
                          {warnings}
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

    private SqlHarnessOutcome Success(object report) =>
        new(SqlHarnessExitCode.Success, report, null);

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