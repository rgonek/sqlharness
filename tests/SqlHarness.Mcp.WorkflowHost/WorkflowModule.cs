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
        return Persist(BuildPairReport());
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
            var report = BuildPairReport($"matrix-{index:D2}-{value}", CellVerdict(index));
            var directory = Write(report);
            cells.Add(new CompareMatrixCellReport(index, value, report with { ArtifactDirectory = directory }));
        }

        _writer.WriteMatrixIndex(cells);
        _lastArtifactDirectory = cells[0].Compare.ArtifactDirectory;
        return Success(new SqlHarnessCompareMatrixReport(_scenario.MatrixName, _scenario.MatrixType, cells));
    }

    private SqlHarnessOutcome Persist(SqlHarnessCompareReport report)
    {
        var directory = Write(report);
        _lastArtifactDirectory = directory;
        return Success(report with { ArtifactDirectory = directory });
    }

    private string Write(SqlHarnessCompareReport report)
    {
        var runs = BuildRuns(report);
        return _writer.Write(report, runs, _scenario.ProfileName, _owner);
    }

    private SqlHarnessCompareReport BuildPairReport(string? label = null, string? verdict = null)
    {
        var target = Target();
        var baseline = Variant("baseline", 40, 12, warning: false);
        var candidate = Variant("candidate", 12, 4, warning: _scenario.Id == WorkflowScenarios.CollidingOperatorsId);
        if (_scenario.Id == WorkflowScenarios.UnavailableMetricsId)
        {
            baseline = PostgresVariant("baseline", 0, 0.8m);
            candidate = PostgresVariant("candidate", 0, 0.4m);
        }

        var equivalent = _scenario.Id is WorkflowScenarios.NoImprovementId or WorkflowScenarios.RequestScopeId
            ? false
            : true;
        var regression = _scenario.Id is WorkflowScenarios.NoImprovementId or WorkflowScenarios.RequestScopeId
            ? new RegressionReport(
                "inconclusive",
                ["result-equivalence", "baseline-unstable"],
                RegressionPolicy.PolicyVersion,
                new RegressionDiagnostic("inconclusive", 40, 12, -28, -70),
                new RegressionDiagnostic("pass", 12, 4, -8, -66.7m),
                new RegressionDiagnostic("unavailable", null, null, null, null))
            : new RegressionReport(
                verdict ?? "pass",
                [],
                RegressionPolicy.PolicyVersion,
                new RegressionDiagnostic("pass", 40, 12, -28, -70),
                new RegressionDiagnostic("pass", 12, 4, -8, -66.7m),
                new RegressionDiagnostic(
                    _scenario.Id == WorkflowScenarios.UnavailableMetricsId ? "unavailable" : "pass",
                    _scenario.Id == WorkflowScenarios.UnavailableMetricsId ? null : 40,
                    _scenario.Id == WorkflowScenarios.UnavailableMetricsId ? null : 12,
                    _scenario.Id == WorkflowScenarios.UnavailableMetricsId ? null : -28,
                    _scenario.Id == WorkflowScenarios.UnavailableMetricsId ? null : -70));

        return new SqlHarnessCompareReport(
            target, 5, 10, equivalent, baseline, candidate, null)
        {
            Equivalence = new ResultEquivalenceReport(
                ResultComparisonMode.Ordered,
                equivalent,
                equivalent ? 0 : 3,
                equivalent ? 0 : 1,
                equivalent ? 0 : 1,
                MismatchReason: equivalent ? null : "rows"),
            Regression = regression,
            Parameters = label is null
                ? [new BenchmarkParameterReport("Region", "nvarchar", 32, null, null)]
                : [new BenchmarkParameterReport("BatchSize", "int", null, null, null)],
        };
    }

    private static string CellVerdict(int index) =>
        index switch
        {
            7 => "fail",
            15 => "inconclusive",
            _ => "pass",
        };

    private IReadOnlyList<CompareRunArtifact> BuildRuns(SqlHarnessCompareReport report)
    {
        var baselinePlan = PlanXml(_scenario.BaselineSql, warningOnSecond: false);
        var candidatePlan = PlanXml(_scenario.CandidateSql, warningOnSecond: _scenario.Id == WorkflowScenarios.CollidingOperatorsId);
        var runs = new List<CompareRunArtifact>(10);
        for (var repetition = 1; repetition <= 5; repetition++)
        {
            runs.Add(Run("baseline", repetition, 40 + repetition, 12, baselinePlan, report.Baseline.MetricReport));
            runs.Add(Run("candidate", repetition, 12 + repetition, 4, candidatePlan, report.Candidate.MetricReport));
        }

        return runs;
    }

    private static CompareRunArtifact Run(
        string variant, int repetition, long elapsed, long reads, string plan, BenchmarkMetricReport? metrics) =>
        new(variant, repetition, metrics?.CpuTimeAvailability == BenchmarkMetricReport.Unavailable ? 0 : elapsed,
            elapsed, reads, new Dictionary<string, long> { ["Orders"] = reads }, "hash-" + variant,
            [plan], 0, Metrics: metrics is null ? null : new BenchmarkRunMetrics(
                metrics.CpuTimeAvailability,
                metrics.ElapsedTimeAvailability,
                metrics.ElapsedTimeMillisecondsExact?.Median,
                metrics.ElapsedWholeMillisecondsAreExact,
                null,
                metrics.ElapsedTimeMillisecondsExact?.Median,
                metrics.LogicalReadsAvailability,
                metrics.LogicalReadsSource,
                metrics.RelationBufferSource,
                metrics.RelationBuffersAreAdditive,
                metrics.ResultRowSource,
                metrics.ResultRowSourceDescription,
                metrics.Warnings));

    private static CompareVariantReport Variant(string name, long elapsed, long reads, bool warning) => new(
        name,
        new CompareDistribution(elapsed - 2, elapsed, elapsed + 2),
        new CompareDistribution(elapsed - 2, elapsed, elapsed + 2),
        new CompareDistribution(reads, reads, reads),
        new Dictionary<string, long> { ["Orders"] = reads },
        [
            new CompareOperatorReport(1, "Index Seek", "[dbo].[Orders]", warning, warning, false),
            new CompareOperatorReport(1, "Clustered Index Scan", "[dbo].[Accounts]", false, false, false),
        ],
        warning ? ["SpillToTempDb"] : [])
    {
        LogicalReadsByTable = new Dictionary<string, CompareDistribution> { ["Orders"] = new(reads, reads, reads) },
    };

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
        return $"""
                    <StmtSimple StatementText="{escaped}">
                      <QueryPlan>
                        <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="7" EstimatedTotalSubtreeCost="0.6">
                          {warnings}
                          <IndexScan>
                            <Object Table="[dbo].[Orders]" Index="[IX_Orders]" />
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