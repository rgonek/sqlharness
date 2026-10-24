using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.WorkflowHost;

public sealed record WorkflowScenario(
    string Id,
    string Family,
    int Seed,
    bool RequestScope,
    string Engine,
    string BaselineSql,
    string CandidateSql,
    string? MatrixName,
    string? MatrixType,
    IReadOnlyList<string>? MatrixValues)
{
    public string ProfileName => Engine == "postgres" ? "workflow-fixture-pg" : WorkflowScenarios.SqlServerProfile;
}

public static class WorkflowScenarios
{
    public const string SqlServerProfile = "workflow-fixture";
    public const string PostgresProfile = "workflow-fixture-pg";

    public const string NoImprovementId = "no-improvement";
    public const string PartialMatrixId = "partial-matrix-32";
    public const string CollidingOperatorsId = "colliding-operators";
    public const string UnavailableMetricsId = "unavailable-metrics";
    public const string RequestScopeId = "request-scope-mapping";
    public const string PlanPagingId = "plan-paging-513";
    public const string ArtifactReviewStudyId = "artifact-review-study";
    public const string ArtifactReviewStudyV2Id = "artifact-review-study-v2";
    public const string ArtifactDiagnosticEvidenceId = "artifact-diagnostic-evidence";

    public const string ArtifactReviewBaselineSql =
        "SELECT Id, Region FROM dbo.Orders WHERE Batch = @Batch; SELECT COUNT_BIG(*) FROM dbo.Orders; SELECT Id FROM dbo.Customers; SELECT SUM(Amount) FROM dbo.Payments;";
    public const string ArtifactReviewSetupSql = "SET NOCOUNT ON;";
    public const string ArtifactReviewCandidateV1Sql =
        "SELECT Id, Region FROM dbo.Orders WHERE Batch = @Batch; SELECT COUNT_BIG(*) FROM dbo.Orders; SELECT Id FROM dbo.Customers; SELECT SUM(Amount) FROM dbo.Payments WHERE Type = 1;";
    public const string ArtifactReviewCandidateV2Sql =
        "SELECT Id, Region FROM dbo.Orders WHERE Batch = @Batch; SELECT COUNT_BIG(*) FROM dbo.Orders; SELECT Id FROM dbo.Customers; SELECT SUM(Amount) FROM dbo.Payments WHERE Type = 2;";
    public const string ArtifactReviewQuerySql =
        "SELECT * FROM dbo.LargeFixture; SELECT * FROM dbo.AssertionFixture; SELECT * FROM dbo.FinalAssertionFixture;";

    public const string NoImprovementBaselineSql =
        "SELECT Id, Name FROM dbo.Orders WHERE Region = @Region ORDER BY Id;";
    public const string NoImprovementCandidateSql =
        "SELECT Id, Name FROM dbo.Orders WHERE Region = @Region ORDER BY Id OPTION (FAST 1);";
    public const string PartialMatrixBaselineSql =
        "SELECT Id, Amount FROM dbo.Orders WHERE BatchSize = @BatchSize ORDER BY Id;";
    public const string PartialMatrixCandidateSql =
        "SELECT Id, Amount FROM dbo.Orders WHERE BatchSize = @BatchSize ORDER BY Id OPTION (RECOMPILE);";
    public const string CollidingBaselineSql =
        "SELECT a.Id FROM dbo.Accounts AS a JOIN dbo.Ledger AS l ON a.Id = l.AccountId; SELECT COUNT_BIG(*) FROM dbo.Accounts;";
    public const string CollidingCandidateSql =
        "SELECT a.Id FROM dbo.Accounts AS a JOIN dbo.Ledger AS l ON a.Id = l.AccountId; SELECT COUNT_BIG(*) FROM dbo.Accounts WHERE a.Active = 1;";
    public const string UnavailableBaselineSql =
        "SELECT id, region FROM public.orders WHERE region = $1 ORDER BY id;";
    public const string UnavailableCandidateSql =
        "SELECT id, region FROM public.orders WHERE region = $1 ORDER BY id LIMIT 100;";

    public static readonly IReadOnlyList<WorkflowScenario> All =
    [
        new(NoImprovementId, "no-improvement-invalid-comparison", 7701, false, "sqlserver",
            NoImprovementBaselineSql, NoImprovementCandidateSql, null, null, null),
        new(PartialMatrixId, "partial-32-case-matrix", 7702, false, "sqlserver",
            PartialMatrixBaselineSql, PartialMatrixCandidateSql, "BatchSize", "int",
            Enumerable.Range(1, 32).Select(value => value.ToString()).ToArray()),
        new(CollidingOperatorsId, "colliding-operator-ids", 7703, false, "sqlserver",
            CollidingBaselineSql, CollidingCandidateSql, null, null, null),
        new(UnavailableMetricsId, "unavailable-metrics", 7704, false, "postgres",
            UnavailableBaselineSql, UnavailableCandidateSql, null, null, null),
        new(RequestScopeId, "request-scope-mapping", 7705, true, "sqlserver",
            NoImprovementBaselineSql, NoImprovementCandidateSql, null, null, null),
        new(PlanPagingId, "saved-plan-paging-513", 7706, false, "sqlserver",
            "SELECT Id FROM dbo.Orders;", "SELECT Id FROM dbo.Orders;", null, null, null),
        new(ArtifactReviewStudyId, "artifact-review-study", 7707, false, "sqlserver",
            ArtifactReviewBaselineSql, ArtifactReviewCandidateV1Sql, null, null, null),
        new(ArtifactReviewStudyV2Id, "artifact-review-study", 7708, false, "sqlserver",
            ArtifactReviewBaselineSql, ArtifactReviewCandidateV2Sql, null, null, null),
        new(ArtifactDiagnosticEvidenceId, "artifact-diagnostic-evidence", 7709, false, "sqlserver",
            ArtifactReviewBaselineSql, ArtifactReviewCandidateV1Sql, null, null, null),
    ];

    public static WorkflowScenario Require(string id)
    {
        var match = All.FirstOrDefault(scenario => string.Equals(scenario.Id, id, StringComparison.Ordinal));
        if (match is null)
            throw new ArgumentException($"Unknown workflow scenario '{id}'.", nameof(id));
        return match;
    }

    public static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            [SqlServerProfile] = new(
                "workflow-unreachable.invalid",
                "fixturedb",
                new Dictionary<string, string>(),
                "integrated"),
            [PostgresProfile] = new(
                "workflow-pg.invalid",
                "fixturedb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "fixture",
                PasswordEnvVar: "SQLHARNESS_077_FIXTURE_PW",
                Engine: "postgres"),
        };

    public static void WriteInputs(string directory, WorkflowScenario scenario)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "baseline.sql"), scenario.BaselineSql + Environment.NewLine);
        File.WriteAllText(Path.Combine(directory, "candidate.sql"), scenario.CandidateSql + Environment.NewLine);
    }
}