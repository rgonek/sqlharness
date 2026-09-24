using System.Text.Json;

using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class IndexesTests
{
    private const string ProfileSecret = "tenant-indexes-secret-never-emit";
    private const string Filter = "[Status]=(1)";
    private const string FilterA = "SecretFilterPredicate=1";
    private const string FilterB = "OtherSecretFilterPredicate=2";
    private const string PlantedObject = "PlantedObjectName";
    private const string TopMessage = "Index analysis top limit must be between 1 and 500.";
    private const string TimeoutMessage = "SQL timeout must be between 1 and 300 seconds.";
    private const string ObjectMessage = "indexes --object must be a single object name or schema.name.";
    private const string MissingObjectMessage = "indexes object was not found or was ambiguous.";
    private const string MalformedMessage = "Index analysis result is malformed.";
    private const string PostgresMessage = "Index overlap analysis is available only on SQL Server.";
    private const string EvidenceWarning =
        "Missing-index evidence is cumulative since SQL Server start and can be shortened or reset by restart, failover, index DDL, or a DMV clear.";

    [Fact]
    public async Task Indexes_executes_once_and_publishes_a_classified_report()
    {
        var session = Fixture.Session(disabled: true, filter: Filter);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        var report = AssertSuccess(outcome, artifacts, session, Filter);
        Assert.Null(report.ObjectFilter);
        Assert.Equal(20, report.Top);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero), report.ObservationSince);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero), report.ObservedAt);
        Assert.Equal(EvidenceWarning, Assert.Single(report.Warnings));
        var candidate = Assert.Single(report.Candidates);
        var raw = Assert.Single(artifacts.Candidates);
        var index = Assert.Single(artifacts.Indexes);
        var match = IndexOverlapClassifier.FindBest(raw, artifacts.Indexes);
        Assert.Equal(["TenantId"], candidate.EqualityColumns);
        Assert.Equal(["CreatedAt"], candidate.InequalityColumns);
        Assert.Equal(["Name", "Status"], candidate.IncludeColumns);
        Assert.Equal(3L, candidate.UserSeeks);
        Assert.Equal(4L, candidate.UserScans);
        Assert.Equal(2.5m, candidate.AverageTotalUserCost);
        Assert.Equal(50m, candidate.AverageUserImpactPercent);
        Assert.Equal(99.25m, candidate.CumulativeImpactScore);
        Assert.Equal(new DateTimeOffset(Fixture.LastSeekLocal).ToUniversalTime(), candidate.LastUserSeek);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 23, 30, 0, TimeSpan.Zero), candidate.LastUserScan);
        Assert.Equal(match.Classification, candidate.Classification);
        Assert.Equal(IndexOverlapClassification.PartialKey, candidate.Classification);
        Assert.Equal(match.MatchedKeyColumnCount, candidate.MatchedKeyColumnCount);
        Assert.Equal(1, candidate.MatchedKeyColumnCount);
        Assert.Equal(match.CandidateKeyColumnCount, candidate.CandidateKeyColumnCount);
        Assert.Equal(2, candidate.CandidateKeyColumnCount);
        Assert.Equal(match.MissingIncludeColumns, candidate.MissingIncludeColumns);
        Assert.Empty(candidate.MissingIncludeColumns);
        Assert.Equal("IX_Contracts_Tenant", candidate.BestExistingIndex);
        Assert.True(candidate.ExistingIndexDisabled);
        Assert.True(candidate.ExistingIndexHasFilter);
        Assert.Equal(index.FilterHash, candidate.ExistingIndexFilterHash);
        Assert.NotEqual(Filter, candidate.ExistingIndexFilterHash);
        Assert.Equal(Filter, Assert.Single(artifacts.Sensitive).FilterDefinition);
        Assert.Equal("db-acme", artifacts.Target);
        Assert.Equal(raw.CandidateId, candidate.CandidateId);

        var command = Assert.Single(session.Commands);
        Assert.Equal(IndexAnalysisQuery.Sql, command.Sql);
        Assert.Equal(30, command.TimeoutSeconds);
        Assert.Equal(IndexAnalysisQuery.Parameters(20, null, null), command.Parameters);
        AssertNoQueryOrDdl(JsonSerializer.Serialize(report));
        Assert.DoesNotContain(Filter, JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indexes_classifies_every_candidate_against_the_collected_indexes()
    {
        var session = Fixture.Session(
            filter: Filter,
            extraCandidates: [Fixture.Candidate(
                candidateId: Fixture.Cell.Of(12L),
                table: Fixture.Cell.Of("Invoices"),
                equality: Fixture.Cell.Of("[invoiceid]"),
                inequality: Fixture.Cell.Of(null),
                included: Fixture.Cell.Of(null))],
            extraCatalog: [Fixture.Catalog("dbo", "Invoices", "InvoiceId")]);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        var report = AssertSuccess(outcome, artifacts, session, Filter);
        Assert.Equal(2, report.Candidates.Count);
        Assert.Equal(2, artifacts.Candidates.Count);
        Assert.Single(artifacts.Indexes);
        Assert.Equal(
            report.Candidates.Select(candidate => candidate.CandidateId),
            artifacts.Candidates.Select(candidate => candidate.CandidateId));
        Assert.Equal(IndexOverlapClassification.PartialKey, report.Candidates[0].Classification);
        Assert.Equal("IX_Contracts_Tenant", report.Candidates[0].BestExistingIndex);
        var second = report.Candidates[1];
        var secondMatch = IndexOverlapClassifier.FindBest(artifacts.Candidates[1], artifacts.Indexes);
        Assert.Equal(IndexOverlapClassification.NewShape, second.Classification);
        Assert.Equal(secondMatch.Classification, second.Classification);
        Assert.Equal(secondMatch.MatchedKeyColumnCount, second.MatchedKeyColumnCount);
        Assert.Equal(secondMatch.CandidateKeyColumnCount, second.CandidateKeyColumnCount);
        Assert.Equal(secondMatch.MissingIncludeColumns, second.MissingIncludeColumns);
        Assert.Null(second.BestExistingIndex);
        Assert.Null(second.ExistingIndexDisabled);
        Assert.Null(second.ExistingIndexHasFilter);
        Assert.Null(second.ExistingIndexFilterHash);
    }

    [Fact]
    public async Task Indexes_object_filter_uses_the_first_candidate_spelling()
    {
        var session = Fixture.Session(filter: Filter);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(objectName: "DBO.contracts"));

        var report = AssertSuccess(outcome, artifacts, session, Filter);
        Assert.Equal("dbo.Contracts", report.ObjectFilter);
        var command = Assert.Single(session.Commands);
        Assert.Equal(IndexAnalysisQuery.Parameters(20, "DBO", "contracts"), command.Parameters);
    }

    [Fact]
    public async Task Indexes_unqualified_object_binds_a_null_schema()
    {
        var session = Fixture.Session(filter: Filter);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(objectName: "Contracts"));

        var report = AssertSuccess(outcome, artifacts, session, Filter);
        Assert.Equal("dbo.Contracts", report.ObjectFilter);
        Assert.Equal(IndexAnalysisQuery.Parameters(20, null, "Contracts"), Assert.Single(session.Commands).Parameters);
    }

    [Fact]
    public async Task Indexes_empty_database_wide_telemetry_succeeds_and_writes()
    {
        var session = Fixture.Empty(matchCount: 0);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        var report = AssertSuccess(outcome, artifacts, session);
        Assert.Empty(report.Candidates);
        Assert.Null(report.ObjectFilter);
        Assert.Empty(artifacts.Candidates);
        Assert.Empty(artifacts.Indexes);
        Assert.Empty(artifacts.Sensitive);
        Assert.Equal(IndexAnalysisQuery.Parameters(20, null, null), Assert.Single(session.Commands).Parameters);
    }

    [Fact]
    public async Task Indexes_empty_object_telemetry_uses_the_requested_object_string()
    {
        var session = Fixture.Empty(matchCount: 1);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(objectName: "DBO.contracts"));

        var report = AssertSuccess(outcome, artifacts, session);
        Assert.Empty(report.Candidates);
        Assert.Equal("DBO.contracts", report.ObjectFilter);
        Assert.Equal(IndexAnalysisQuery.Parameters(20, "DBO", "contracts"), Assert.Single(session.Commands).Parameters);
        Assert.Equal(1, artifacts.WriteCalls);
    }

    [Fact]
    public async Task Indexes_new_shape_has_null_best_index_fields()
    {
        var session = Fixture.Session(
            indexType: "HEAP",
            indexName: "heap",
            indexId: 0,
            filter: null,
            disabled: false,
            equality: "[tenantid]",
            inequality: null,
            included: null);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        var candidate = Assert.Single(AssertSuccess(outcome, artifacts, session).Candidates);
        Assert.Equal(IndexOverlapClassification.NewShape, candidate.Classification);
        Assert.Null(candidate.BestExistingIndex);
        Assert.Null(candidate.ExistingIndexDisabled);
        Assert.Null(candidate.ExistingIndexHasFilter);
        Assert.Null(candidate.ExistingIndexFilterHash);
        Assert.Equal(0, candidate.MatchedKeyColumnCount);
        Assert.Equal(1, candidate.CandidateKeyColumnCount);
        Assert.Empty(candidate.MissingIncludeColumns);
        Assert.Equal("HEAP", Assert.Single(artifacts.Indexes).Type);
    }

    [Fact]
    public async Task Indexes_empty_best_index_name_keeps_index_flags()
    {
        var session = Fixture.Session(
            indexName: null,
            filter: null,
            disabled: false,
            equality: "[tenantid]",
            inequality: null,
            included: null);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        var candidate = Assert.Single(AssertSuccess(outcome, artifacts, session).Candidates);
        Assert.Equal(IndexOverlapClassification.Covered, candidate.Classification);
        Assert.Null(candidate.BestExistingIndex);
        Assert.False(candidate.ExistingIndexDisabled);
        Assert.False(candidate.ExistingIndexHasFilter);
        Assert.Null(candidate.ExistingIndexFilterHash);
        Assert.Equal(string.Empty, Assert.Single(artifacts.Indexes).Name);
    }

    [Theory]
    [InlineData(0, 30, null, TopMessage)]
    [InlineData(501, 30, null, TopMessage)]
    [InlineData(-1, 30, null, TopMessage)]
    [InlineData(20, 0, null, TimeoutMessage)]
    [InlineData(20, 301, null, TimeoutMessage)]
    [InlineData(20, -1, null, TimeoutMessage)]
    [InlineData(0, 0, "a.b.c", TopMessage)]
    [InlineData(20, 0, "a.b.c", TimeoutMessage)]
    [InlineData(20, 30, "a.b.c", ObjectMessage)]
    [InlineData(20, 30, "", ObjectMessage)]
    [InlineData(20, 30, " ", ObjectMessage)]
    [InlineData(20, 30, ".Contracts", ObjectMessage)]
    [InlineData(20, 30, "dbo.", ObjectMessage)]
    [InlineData(20, 30, "dbo..Contracts", ObjectMessage)]
    public async Task Indexes_rejects_invalid_input_before_resolve(int top, int timeout, string? objectName, string message)
    {
        var session = Fixture.Session();
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts, profiles: ThrowingProfiles).ExecuteAsync(
            Operation(top: top, timeoutSeconds: timeout, objectName: objectName));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(message, outcome.SafeError);
        Assert.Null(outcome.Report);
        Assert.Equal(0, session.ConnectCalls);
        Assert.Empty(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(500, 300)]
    public async Task Indexes_accepts_inclusive_bounds(int top, int timeout)
    {
        var session = Fixture.Session();
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(top: top, timeoutSeconds: timeout));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, session.ConnectCalls);
        var command = Assert.Single(session.Commands);
        Assert.Equal(timeout, command.TimeoutSeconds);
        Assert.Equal(IndexAnalysisQuery.Parameters(top, null, null), command.Parameters);
    }

    [Fact]
    public async Task Indexes_authentication_failure_returns_three_without_the_batch()
    {
        var session = Fixture.Session();
        session.ConnectFailure = new AzureCliException("not logged in");
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.Authentication, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
        AssertNoQueryOrDdl(outcome.SafeError);
    }

    [Fact]
    public async Task Indexes_target_mismatch_returns_four_without_the_batch()
    {
        var session = Fixture.Session();
        session.ConnectFailure = new SqlTargetMismatchException("mismatch");
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
        AssertNoQueryOrDdl(outcome.SafeError);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(2L)]
    [InlineData(-1L)]
    public async Task Indexes_missing_or_ambiguous_object_returns_two(long matchCount)
    {
        var session = Fixture.Session(matchCount: matchCount, table: PlantedObject, filter: Filter);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(objectName: "dbo.Contracts"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(MissingObjectMessage, outcome.SafeError);
        Assert.Null(outcome.Report);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Single(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.DoesNotContain(PlantedObject, outcome.SafeError, StringComparison.Ordinal);
        Assert.DoesNotContain(Filter, outcome.SafeError, StringComparison.Ordinal);
        AssertNoQueryOrDdl(outcome.SafeError);
    }

    [Fact]
    public async Task Indexes_negative_database_wide_match_count_is_malformed()
    {
        var session = Fixture.Empty(matchCount: -1);
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Equal(MalformedMessage, outcome.SafeError);
        Assert.Null(outcome.Report);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.Single(session.Commands);
        Assert.DoesNotContain("-1", outcome.SafeError, StringComparison.Ordinal);
        AssertNoQueryOrDdl(outcome.SafeError);
    }

    [Fact]
    public async Task Indexes_permission_timeout_malformed_shape_and_sql_exception_return_five()
    {
        await AssertSqlPhaseFailure(Fixture.Failing(new UnauthorizedAccessException("VIEW DATABASE STATE permission denied")));
        await AssertSqlPhaseFailure(Fixture.Failing(new TimeoutException("query timeout")));
        await AssertSqlPhaseFailure(Fixture.Malformed());
        await AssertSqlPhaseFailure(Fixture.Failing(FakeSqlException()));
    }

    [Fact]
    public async Task Indexes_writer_failure_returns_six_and_redacts_filters_and_sql()
    {
        var session = Fixture.Session(
            filter: FilterA,
            extraIndexes: [Fixture.Index(indexId: Fixture.Cell.Of(3), name: Fixture.Cell.Of("IX_Other"), filter: Fixture.Cell.Of(FilterB))]);
        var artifacts = new ThrowingIndexAnalysisArtifactWriter();

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(target: SecretTarget()));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(1, artifacts.WriteCalls);
        Assert.Contains(FilterA, artifacts.Leaked, StringComparison.Ordinal);
        Assert.Contains(FilterB, artifacts.Leaked, StringComparison.Ordinal);
        Assert.DoesNotContain(FilterA, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(FilterB, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfileSecret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        AssertNoQueryOrDdl(outcome.SafeError);
    }

    [Fact]
    public async Task Indexes_caller_cancellation_returns_five()
    {
        using var cancellation = new CancellationTokenSource();
        var session = Fixture.Session();
        session.CancelOnExecute = cancellation;
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(), cancellation.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Single(session.Commands);
        AssertNoQueryOrDdl(outcome.SafeError);
    }

    [Fact]
    public async Task Indexes_redacts_secrets_and_omits_query_sql_from_safe_error()
    {
        var session = Fixture.Session();
        session.ExecuteFailure = new InvalidOperationException(
            $"failed {ProfileSecret} batch {IndexAnalysisQuery.Sql}");
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts).ExecuteAsync(Operation(target: SecretTarget()));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.DoesNotContain(ProfileSecret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        AssertNoQueryOrDdl(outcome.SafeError);
        Assert.Equal(0, artifacts.WriteCalls);
    }

    [Fact]
    public async Task Indexes_postgres_does_not_execute_the_batch()
    {
        var session = Fixture.Session();
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts, profiles: PostgresProfiles).ExecuteAsync(
            new SqlHarnessIndexesOperation(
                new SqlTargetRequest("pg", new Dictionary<string, string>()),
                20,
                null,
                30));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Empty(session.Commands);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.Equal(PostgresMessage, outcome.SafeError);
        AssertNoQueryOrDdl(outcome.SafeError);
    }

    [Fact]
    public async Task Indexes_success_receipt_stores_indexes_and_nonzero_raw_footprint()
    {
        var session = Fixture.Session();
        var gain = new FakeGain();
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");

        var outcome = await Module(session, artifacts, gain).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Empty(gain.Records);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(10, 1));
        var record = Assert.Single(gain.Records);
        Assert.Equal("indexes", record.Command);
        Assert.True(record.Success);
        Assert.True(record.RawBytes > 0);
        Assert.True(record.RawLines > 0);
    }

    [Fact]
    public async Task Indexes_gain_append_failure_completes_as_local_storage()
    {
        var session = Fixture.Session();
        var gain = new FakeGain(new IOException("disk full"));

        var outcome = await Module(session, new CapturingIndexAnalysisArtifactWriter("index-artifacts"), gain)
            .ExecuteAsync(Operation());

        var completion = await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(8, 1));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, completion);
    }

    private static SqlHarnessIndexesReport AssertSuccess(
        SqlHarnessOutcome outcome,
        CapturingIndexAnalysisArtifactWriter artifacts,
        FakeSession session,
        params string[] hidden)
    {
        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Null(outcome.SafeError);
        var report = Assert.IsType<SqlHarnessIndexesReport>(outcome.Report);
        Assert.Equal("index-artifacts", report.ArtifactDirectory);
        Assert.Equal(EvidenceWarning, Assert.Single(report.Warnings));
        Assert.Same(session.Identity, report.Target);
        Assert.NotNull(artifacts.Report);
        Assert.Null(artifacts.Report.ArtifactDirectory);
        Assert.Equal(1, artifacts.WriteCalls);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Single(session.Commands);
        var json = JsonSerializer.Serialize(report);
        AssertNoQueryOrDdl(json);
        foreach (var secret in hidden)
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        return report;
    }

    private static async Task AssertSqlPhaseFailure(FakeSession session)
    {
        var artifacts = new CapturingIndexAnalysisArtifactWriter("index-artifacts");
        var outcome = await Module(session, artifacts).ExecuteAsync(Operation());

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(0, artifacts.WriteCalls);
        Assert.Single(session.Commands);
        AssertNoQueryOrDdl(outcome.SafeError);
    }

    private static void AssertNoQueryOrDdl(string? text)
    {
        var value = text ?? string.Empty;
        Assert.DoesNotContain(IndexAnalysisQuery.Sql, value, StringComparison.Ordinal);
        Assert.DoesNotContain("dm_db_missing_index", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE INDEX", value, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX", value, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER INDEX", value, StringComparison.Ordinal);
    }

    private static SqlHarnessIndexesOperation Operation(
        int top = 20,
        int timeoutSeconds = 30,
        string? objectName = null,
        SqlTargetRequest? target = null) =>
        new(target ?? Target(), top, objectName, timeoutSeconds);

    private static SqlHarnessModule Module(
        FakeSession session,
        IIndexAnalysisArtifactWriter? artifacts = null,
        FakeGain? gain = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? profiles = null) =>
        new(session, gain ?? new FakeGain(), profiles ?? Profiles, indexAnalysisArtifacts: artifacts);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["tenant"] = "acme" });

    private static SqlTargetRequest SecretTarget() =>
        new("test", new Dictionary<string, string> { ["tenant"] = ProfileSecret });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new(
                "server",
                "db-{tenant}",
                new Dictionary<string, string> { ["tenant"] = "^.+$" },
                "integrated"),
        };

    private static IReadOnlyDictionary<string, TargetProfile> ThrowingProfiles() =>
        throw new InvalidOperationException("profiles must not load");

    private static IReadOnlyDictionary<string, TargetProfile> PostgresProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["pg"] = new(
                "localhost",
                "appdb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
                TrustServerCertificate: true,
                Engine: "postgres"),
        };

    private static SqlException FakeSqlException() =>
        (SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SqlException));

    private sealed class FakeGain(Exception? appendFailure = null) : IGainStore
    {
        public List<GainRecord> Records { get; } = [];

        public void Append(GainRecord record)
        {
            if (appendFailure is not null)
                throw appendFailure;
            Records.Add(record);
        }

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class CapturingIndexAnalysisArtifactWriter(string directory) : IIndexAnalysisArtifactWriter
    {
        public List<IndexCandidate> Candidates { get; } = [];

        public List<ExistingIndex> Indexes { get; } = [];

        public List<SensitiveExistingIndex> Sensitive { get; } = [];

        public SqlHarnessIndexesReport? Report { get; private set; }

        public string? Target { get; private set; }

        public int WriteCalls { get; private set; }

        public string Write(
            SqlHarnessIndexesReport report,
            IReadOnlyList<IndexCandidate> candidates,
            IReadOnlyList<ExistingIndex> indexes,
            IReadOnlyList<SensitiveExistingIndex> sensitiveIndexes,
            string target)
        {
            WriteCalls++;
            Report = report;
            Target = target;
            Candidates.AddRange(candidates);
            Indexes.AddRange(indexes);
            Sensitive.AddRange(sensitiveIndexes);
            return directory;
        }
    }

    private sealed class ThrowingIndexAnalysisArtifactWriter : IIndexAnalysisArtifactWriter
    {
        public int WriteCalls { get; private set; }

        public string Leaked { get; private set; } = string.Empty;

        public string Write(
            SqlHarnessIndexesReport report,
            IReadOnlyList<IndexCandidate> candidates,
            IReadOnlyList<ExistingIndex> indexes,
            IReadOnlyList<SensitiveExistingIndex> sensitiveIndexes,
            string target)
        {
            WriteCalls++;
            Leaked = string.Join(
                " ",
                sensitiveIndexes.Select(sensitive => sensitive.FilterDefinition));
            throw new IOException($"disk unavailable {Leaked} {ProfileSecret} {IndexAnalysisQuery.Sql}");
        }
    }

    private sealed class FakeSession(ISqlReader reader) : ISqlSessionFactory, ISqlSession
    {
        public Exception? ConnectFailure { get; set; }

        public Exception? ExecuteFailure { get; set; }

        public CancellationTokenSource? CancelOnExecute { get; set; }

        public int ConnectCalls { get; private set; }

        public List<SqlExecutionCommand> Commands { get; } = [];

        public IReadOnlyList<string> Messages => [];

        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "db-acme", "server", "db-acme", "profile");

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectCalls++;
            return ConnectFailure is null
                ? Task.FromResult<ISqlSession>(this)
                : Task.FromException<ISqlSession>(ConnectFailure);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (CancelOnExecute is not null)
            {
                CancelOnExecute.Cancel();
                return Task.FromException<ISqlReader>(new OperationCanceledException(ct));
            }

            if (ExecuteFailure is not null)
                return Task.FromException<ISqlReader>(ExecuteFailure);
            return Task.FromResult(reader);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class Fixture
    {
        public static readonly DateTime ObservationSince = new(2026, 7, 29, 1, 2, 3, DateTimeKind.Unspecified);

        public static readonly DateTimeOffset ObservedAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.FromHours(2));

        public static readonly DateTime LastSeekLocal = new(2026, 7, 29, 15, 45, 0, DateTimeKind.Local);

        public static readonly DateTime LastScanUtc = new(2026, 7, 28, 23, 30, 0, DateTimeKind.Utc);

        private static readonly string[] ObservationColumns =
        [
            "observation_since",
            "observed_at",
            "object_match_count",
        ];

        private static readonly string[] CandidateColumns =
        [
            "candidate_id",
            "schema_name",
            "table_name",
            "equality_columns",
            "inequality_columns",
            "included_columns",
            "user_seeks",
            "user_scans",
            "avg_total_user_cost",
            "avg_user_impact",
            "cumulative_impact_score",
            "last_user_seek",
            "last_user_scan",
        ];

        private static readonly string[] CatalogColumns = ["schema_name", "table_name", "column_name"];

        private static readonly string[] IndexHeaderColumns =
        [
            "schema_name",
            "table_name",
            "index_id",
            "index_name",
            "type_desc",
            "is_unique",
            "is_primary_key",
            "is_unique_constraint",
            "is_disabled",
            "filter_definition",
        ];

        private static readonly string[] IndexColumnColumns =
        [
            "schema_name",
            "table_name",
            "index_id",
            "column_name",
            "key_ordinal",
            "is_included_column",
            "is_descending_key",
            "index_column_id",
        ];

        private static readonly string[] CompressionColumns =
        [
            "schema_name",
            "table_name",
            "index_id",
            "data_compression_desc",
        ];

        public static FakeSession Session(
            long matchCount = 1,
            string? filter = Filter,
            bool disabled = false,
            string? indexName = "IX_Contracts_Tenant",
            string indexType = "NONCLUSTERED",
            int indexId = 2,
            string table = "Contracts",
            string? equality = "[tenantid]",
            string? inequality = "[createdat]",
            string? included = "[name], [status]",
            object?[][]? extraCandidates = null,
            object?[][]? extraCatalog = null,
            object?[][]? extraIndexes = null) =>
            new(Reader(
                matchCount,
                filter,
                disabled,
                indexName,
                indexType,
                indexId,
                table,
                equality,
                inequality,
                included,
                extraCandidates,
                extraCatalog,
                extraIndexes));

        public static FakeSession Empty(long matchCount) =>
            new(new FakeReader(
                ObservationSet(Observation(matchCount)),
                CandidateSet(),
                CatalogSet(),
                IndexSet(),
                IndexColumnSet(),
                CompressionSet()));

        public static FakeSession Malformed() =>
            new(new FakeReader(ObservationSet(Observation(1L))));

        public static FakeSession Failing(Exception failure)
        {
            var session = Session();
            session.ExecuteFailure = failure;
            return session;
        }

        public static object?[] Candidate(
            Cell candidateId = default,
            Cell schema = default,
            Cell table = default,
            Cell equality = default,
            Cell inequality = default,
            Cell included = default) =>
        [
            candidateId.Or(11L),
            schema.Or("dbo"),
            table.Or("Contracts"),
            equality.Or("[tenantid]"),
            inequality.Or("[createdat]"),
            included.Or("[name], [status]"),
            3L,
            4L,
            2.5m,
            50m,
            99.25m,
            LastSeekLocal,
            LastScanUtc,
        ];

        public static object?[] Catalog(string schema, string table, string column) => [schema, table, column];

        public static object?[] Index(
            Cell indexId = default,
            Cell name = default,
            Cell filter = default,
            Cell disabled = default,
            Cell type = default,
            Cell table = default) =>
        [
            "dbo",
            table.Or("Contracts"),
            indexId.Or(2),
            name.Or("IX_Contracts_Tenant"),
            type.Or("NONCLUSTERED"),
            false,
            false,
            false,
            disabled.Or(false),
            filter.Or(Filter),
        ];

        private static ISqlReader Reader(
            long matchCount,
            string? filter,
            bool disabled,
            string? indexName,
            string indexType,
            int indexId,
            string table,
            string? equality,
            string? inequality,
            string? included,
            object?[][]? extraCandidates,
            object?[][]? extraCatalog,
            object?[][]? extraIndexes)
        {
            var candidateRows = new List<object?[]>
            {
                Candidate(
                    table: Cell.Of(table),
                    equality: Cell.Of(equality),
                    inequality: Cell.Of(inequality),
                    included: Cell.Of(included)),
            };
            if (extraCandidates is not null)
                candidateRows.AddRange(extraCandidates);

            var catalogRows = new List<object?[]>
            {
                Catalog("dbo", table, "TenantId"),
                Catalog("dbo", table, "CreatedAt"),
                Catalog("dbo", table, "Name"),
                Catalog("dbo", table, "Status"),
            };
            if (extraCatalog is not null)
                catalogRows.AddRange(extraCatalog);

            var indexRows = new List<object?[]>
            {
                Index(
                    indexId: Cell.Of(indexId),
                    name: Cell.Of(indexName),
                    filter: Cell.Of(filter),
                    disabled: Cell.Of(disabled),
                    type: Cell.Of(indexType),
                    table: Cell.Of(table)),
            };
            if (extraIndexes is not null)
                indexRows.AddRange(extraIndexes);

            var columnRows = new List<object?[]>
            {
                Column(table, indexId, "TenantId", 1, false, false, 1),
            };
            if (extraIndexes is not null)
            {
                foreach (var header in extraIndexes)
                    columnRows.Add(Column(table, header[2]!, "TenantId", 1, false, false, 1));
            }

            var compressionRows = new List<object?[]> { Compression(table, indexId) };
            if (extraIndexes is not null)
            {
                foreach (var header in extraIndexes)
                    compressionRows.Add(Compression(table, header[2]!));
            }

            return new FakeReader(
                ObservationSet(Observation(matchCount)),
                CandidateSet(candidateRows.ToArray()),
                CatalogSet(catalogRows.ToArray()),
                IndexSet(indexRows.ToArray()),
                IndexColumnSet(columnRows.ToArray()),
                CompressionSet(compressionRows.ToArray()));
        }

        private static object?[] Column(
            string table,
            object indexId,
            string column,
            object keyOrdinal,
            object included,
            object descending,
            object indexColumnId) =>
        [
            "dbo",
            table,
            indexId,
            column,
            keyOrdinal,
            included,
            descending,
            indexColumnId,
        ];

        private static object?[] Compression(string table, object indexId) => ["dbo", table, indexId, "PAGE"];

        private static object?[][] ObservationSet(params object?[][] rows) => Set(ObservationColumns, rows);

        private static object?[] Observation(object? matchCount) => [ObservationSince, ObservedAt, matchCount];

        private static object?[][] CandidateSet(params object?[][] rows) => Set(CandidateColumns, rows);

        private static object?[][] CatalogSet(params object?[][] rows) => Set(CatalogColumns, rows);

        private static object?[][] IndexSet(params object?[][] rows) => Set(IndexHeaderColumns, rows);

        private static object?[][] IndexColumnSet(params object?[][] rows) => Set(IndexColumnColumns, rows);

        private static object?[][] CompressionSet(params object?[][] rows) => Set(CompressionColumns, rows);

        private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];

        public readonly record struct Cell(object? Value, bool Specified)
        {
            public static Cell Of(object? value) => new(value, true);

            public object? Or(object? fallback) => Specified ? Value : fallback;
        }
    }

    private sealed class FakeReader(params object?[][][] sets) : ISqlReader
    {
        private int _set;
        private int _row;

        private string[] Names => sets[_set][0].Cast<string>().ToArray();

        private object?[][] Rows => sets[_set].Skip(1).ToArray();

        public int FieldCount => Names.Length;

        public int RecordsAffected => -1;

        public string GetName(int ordinal) => Names[ordinal];

        public Type GetFieldType(int ordinal) => typeof(object);

        public bool GetAllowNull(int ordinal) => true;

        public object GetValue(int ordinal) => Rows[_row - 1][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_row < Rows.Length)
            {
                _row++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++_set < sets.Length)
            {
                _row = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}