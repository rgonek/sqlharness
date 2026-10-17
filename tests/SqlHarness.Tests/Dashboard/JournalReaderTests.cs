using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class JournalReaderTests
{
    private static JournalReader Reader(TempHome home, FakeProcesses? processes = null) =>
        new(home.DatabasePath, processes ?? new FakeProcesses());

    [Fact]
    public void Session_sql_text_reads_only_the_requested_hash_and_prefers_available_text_within_a_session()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath, storeSensitive: true);
        var session = JournalSeed.Session("cli:session-sql");
        var first = seed.Compare(session, "orders", "whole", "sha256:" + new string('a', 64), "sha256:" + new string('b', 64));
        var second = seed.Compare(session, "orders", "whole", "sha256:" + new string('c', 64), "sha256:" + new string('d', 64));
        var other = seed.Compare(JournalSeed.Session("cli:other-session"), "orders", "whole", "sha256:" + new string('e', 64), "sha256:" + new string('f', 64));
        var hashOnly = seed.Compare(session, "orders", "whole", "sha256:" + new string('1', 64), "sha256:" + new string('2', 64));
        var oversized = seed.Compare(session, "orders", "whole", "sha256:" + new string('3', 64), "sha256:" + new string('4', 64));
        var sharedHash = seed.Compare(session, "orders", "whole", "sha256:" + new string('5', 64), "sha256:" + new string('5', 64));
        var exactByteLimit = seed.Compare(session, "orders", "whole", "sha256:" + new string('6', 64), "sha256:" + new string('7', 64));
        SetSqlText(home.DatabasePath, first.OperationId, "baseline SQL", "candidate SQL");
        SetSqlText(home.DatabasePath, second.OperationId, "candidate SQL v1", "candidate SQL v2");
        SetSqlText(home.DatabasePath, other.OperationId, "private other session", "private other session candidate");
        SetSqlText(home.DatabasePath, hashOnly.OperationId, null, null);
        SetSqlText(home.DatabasePath, oversized.OperationId, new string('x', 300 * 1024), null);
        SetSqlText(home.DatabasePath, sharedHash.OperationId, null, "candidate wins when baseline text is absent");
        var atLimitText = new string('界', 256 * 1024 / 3) + "a";
        var multibyteOverLimitText = new string('界', 256 * 1024 / 3) + "ab";
        SetSqlText(home.DatabasePath, exactByteLimit.OperationId, atLimitText, multibyteOverLimitText);

        var sessionId = Assert.Single(Reader(home).Sessions(new SessionQuery()).Items, item => item.SessionKey == "cli:session-sql").Id;
        var reader = Reader(home);

        Assert.Equal("baseline SQL", reader.SessionSqlText(sessionId, "sha256:" + new string('a', 64))!.Text);
        Assert.Equal("candidate SQL v2", reader.SessionSqlText(sessionId, "sha256:" + new string('d', 64))!.Text);
        Assert.Null(reader.SessionSqlText(sessionId, "sha256:" + new string('e', 64)));
        var missingText = reader.SessionSqlText(sessionId, "sha256:" + new string('1', 64))!;
        Assert.Null(missingText.Text);
        Assert.False(missingText.TooLarge);
        var tooLarge = reader.SessionSqlText(sessionId, "sha256:" + new string('3', 64))!;
        Assert.Null(tooLarge.Text);
        Assert.True(tooLarge.TooLarge);
        Assert.Equal("candidate wins when baseline text is absent", reader.SessionSqlText(sessionId, "sha256:" + new string('5', 64))!.Text);
        var exactLimit = reader.SessionSqlText(sessionId, "sha256:" + new string('6', 64))!;
        Assert.Equal(256 * 1024, System.Text.Encoding.UTF8.GetByteCount(exactLimit.Text!));
        Assert.False(exactLimit.TooLarge);
        var multibyteOverLimit = reader.SessionSqlText(sessionId, "sha256:" + new string('7', 64))!;
        Assert.Null(multibyteOverLimit.Text);
        Assert.True(multibyteOverLimit.TooLarge);
    }

    private static void SetSqlText(string databasePath, long operationId, string? baseline, string? candidate)
    {
        using var connection = OpenSeedDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE operations SET sql_text = $baseline, candidate_sql_text = $candidate WHERE id = $id;";
        command.Parameters.AddWithValue("$baseline", (object?)baseline ?? DBNull.Value);
        command.Parameters.AddWithValue("$candidate", (object?)candidate ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", operationId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Fact]
    public void Agent_kinds_list_only_kinds_present_with_session_counts()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:a", "codex"));
        seed.Operation(JournalSeed.Session("cli:b", "codex"));
        seed.Operation(JournalSeed.Session("cli:c", "copilot"));

        var kinds = Reader(home).AgentKinds();

        Assert.Equal([new KeyCount("codex", 2), new KeyCount("copilot", 1)], kinds);
    }

    [Fact]
    public void Agent_kinds_of_a_missing_journal_are_empty()
    {
        using var home = new TempHome();
        Assert.Empty(Reader(home).AgentKinds());
    }

    [Fact]
    public void Missing_database_reads_as_empty()
    {
        using var home = new TempHome();
        var reader = Reader(home);

        Assert.Null(reader.SchemaVersion());
        Assert.Empty(reader.Sessions(new SessionQuery()).Items);
        Assert.Empty(reader.Operations(new OperationQuery()).Items);
        Assert.Equal(0, reader.Stats(new StatsQuery()).Statuses.Sum(s => s.Count));
    }

    [Fact]
    public void Optimization_evidence_reads_case_metrics_table_io_and_verdicts_from_the_journal()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var handle = seed.Compare(JournalSeed.Session("cli:optimization"), "orders", "whole", "baseline-hash", "candidate-hash");
        var metrics = new BenchmarkJournalRecord(
        [
            OptimizationMetric("baseline", 100, 10, 2) with { Postgres = new PostgresBufferCounters(7, 2, 1, 0, 0, 3) },
            OptimizationMetric("candidate", 80, 8, 1) with { Postgres = new PostgresBufferCounters(5, 1, 0, 0, 0, 1) },
        ], []);
        seed.CompareEvidence(handle, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"regression":{"verdict":"pass"},"cases":[{"caseId":"0","caseLabel":"@CustomerId = 1","equivalence":{"mode":"Ordered","equivalent":true},"regression":{"verdict":"pass"}}]}""",
            metrics);

        var sessionId = Assert.Single(Reader(home).Sessions(new SessionQuery()).Items).Id;
        var evidence = Assert.Single(Reader(home).OptimizationEvidence(sessionId));

        Assert.Equal("orders", evidence.Subject);
        Assert.Equal("whole", evidence.Scope);
        Assert.Equal("db", evidence.Database);
        Assert.Equal("sqlserver", evidence.Engine);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Fingerprint));
        Assert.Equal("baseline-hash", evidence.BaselineHash);
        Assert.Equal("candidate-hash", evidence.CandidateHash);
        Assert.Equal("succeeded", evidence.Status);
        Assert.Equal("pass", evidence.RegressionVerdict);
        Assert.Equal((true, "pass"), (Assert.Single(evidence.Cases).ResultsEquivalent, evidence.Cases[0].RegressionVerdict));
        var baseline = Assert.Single(evidence.Metrics, metric => metric.Variant == "baseline");
        Assert.Equal(100, baseline.LogicalReadsMedian);
        Assert.Equal(10, baseline.ElapsedMedianMs);
        Assert.Equal(10, baseline.CpuMedianMs);
        Assert.Equal(new PostgresBuffers(7, 2, 1, 0, 0, 3), baseline.Postgres);
        Assert.Equal(2, baseline.ScansSum);
        Assert.Equal(new CompareTableIoEvidence("Orders", 90, 10, 2), Assert.Single(baseline.TableRows));

        var section = OptimizationSummaryBuilder.Build([evidence]);
        Assert.Single(section.Subjects);
    }

    [Fact]
    public void Optimization_reader_loads_measure_observations_with_legacy_case_fingerprint_fallback()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:measure-optimization");
        var conditions = JournalSeed.ReproConditions() with
        {
            Cases = [new ConditionsCase("set-a", 0, "set-a", "set-a", [], "case-fallback")],
        };
        var measure = seed.Measure(session, "orders", "whole", "h0", conditions,
            new BenchmarkJournalRecord([new JournalVariantMetrics("measure", "set-a", null, 1, null, null,
                new CompareDistribution(100, 100, 100), null, null, null, null, null, null, 0, false, false, 0,
                [], null, [], [])], []));
        var compare = seed.Compare(session, "orders", "whole", "h0", "h1");
        seed.CompareEvidence(compare, conditions,
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"set-a","caseLabel":"set-a","equivalence":{"equivalent":true}}]}""",
            new BenchmarkJournalRecord([
                OptimizationVariant("baseline", 100, matrixCell: 0) with { CaseId = "set-a" },
                OptimizationVariant("candidate", 80, matrixCell: 0) with { CaseId = "set-a" },
            ], []));
        string? expectedCaseFingerprint;
        using (var connection = new SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE operations SET conditions_json = json_remove(conditions_json, '$.measurementFingerprint', '$.cases[0].measurementFingerprint') WHERE id IN ($measure, $compare);";
            command.Parameters.AddWithValue("$measure", measure.OperationId);
            command.Parameters.AddWithValue("$compare", compare.OperationId);
            Assert.Equal(2, command.ExecuteNonQuery());
            command.CommandText = "SELECT conditions_json FROM operations WHERE id = $measure;";
            command.Parameters.Remove(command.Parameters["$compare"]);
            var legacyJson = (string)command.ExecuteScalar()!;
            using var legacy = JsonDocument.Parse(legacyJson);
            expectedCaseFingerprint = legacy.RootElement.GetProperty("cases")[0].GetProperty("fingerprint").GetString();
            Assert.NotNull(expectedCaseFingerprint);
        }

        var reader = Reader(home);
        var sessionSummary = Assert.Single(reader.Sessions(new SessionQuery(HasCompare: true)).Items);
        var evidence = reader.OptimizationEvidence(sessionSummary.Id);
        var measureEvidence = Assert.Single(evidence, item => item.OperationId == measure.OperationId);
        var compareEvidence = Assert.Single(evidence, item => item.OperationId == compare.OperationId);

        Assert.True(sessionSummary.HasCompare);
        Assert.Equal(compareEvidence.Fingerprint, measureEvidence.Fingerprint);
        Assert.True(measureEvidence.IsMeasure);
        Assert.Equal("h0", measureEvidence.BaselineHash);
        Assert.Null(measureEvidence.CandidateHash);
        Assert.Equal(expectedCaseFingerprint, Assert.Single(measureEvidence.Cases).MeasurementFingerprint);
        Assert.Equal(expectedCaseFingerprint, Assert.Single(measureEvidence.Metrics).MeasurementFingerprint);
        var group = Assert.Single(Assert.Single(OptimizationSummaryBuilder.Build(evidence).Subjects).Groups);
        Assert.Equal(("v0", "v1"), (group.DefaultA, group.DefaultB));
        Assert.Equal([measure.OperationId, compare.OperationId], group.Scopes.Single().Versions.Single(version => version.Version == "v0").MeasuredIn);
    }

    [Fact]
    public void Sessions_filter_has_compare_and_builds_default_optimization_badges()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var withCompare = JournalSeed.Session("cli:has-compare");
        var compare = seed.Compare(withCompare, "orders", "whole", "h0", "h1");
        seed.CompareEvidence(compare, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":true}}]}""",
            OptimizationMetrics(100, 6));
        var withoutCompare = JournalSeed.Session("cli:no-compare");
        seed.Operation(withoutCompare);
        var unassigned = JournalSeed.Session("cli:unassigned-compare");
        seed.Compare(unassigned, null, null, "legacy-a", "legacy-b");
        var measureOnly = JournalSeed.Session("cli:measure-only");
        seed.Measure(measureOnly, "reports", "whole", "measure-hash", JournalSeed.ReproConditions(), JournalSeed.Benchmark());
        var measureWithoutSubject = JournalSeed.Session("cli:measure-without-subject");
        seed.Measure(measureWithoutSubject, null, null, "unassigned-hash", JournalSeed.ReproConditions(), JournalSeed.Benchmark());

        var reader = Reader(home);
        var filtered = reader.Sessions(new SessionQuery(HasCompare: true)).Items;
        Assert.Equal(3, filtered.Count);
        Assert.True(filtered.Single(item => item.SessionKey == "cli:unassigned-compare").HasCompare);
        Assert.Empty(filtered.Single(item => item.SessionKey == "cli:unassigned-compare").Optimization);
        var measureItem = filtered.Single(item => item.SessionKey == "cli:measure-only");
        Assert.Equal("reports", Assert.Single(measureItem.Optimization).Subject);
        var item = filtered.Single(item => item.SessionKey == "cli:has-compare");
        Assert.Equal("cli:has-compare", item.SessionKey);
        Assert.True(item.HasCompare);
        var badge = Assert.Single(item.Optimization);
        Assert.Equal("orders", badge.Subject);
        Assert.Equal("−94%", badge.Change);
        Assert.False(badge.Warning);

        Assert.Equal(["cli:measure-without-subject", "cli:no-compare"],
            reader.Sessions(new SessionQuery(HasCompare: false)).Items.Select(session => session.SessionKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Session_list_optimization_badge_warns_for_results_that_differ()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var compare = seed.Compare(JournalSeed.Session("cli:differing"), "orders", "whole", "h0", "h1");
        seed.CompareEvidence(compare, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":false,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":false}}]}""",
            OptimizationMetrics(100, 6));

        var badge = Assert.Single(Assert.Single(Reader(home).Sessions(new SessionQuery()).Items).Optimization);
        Assert.True(badge.Warning);
    }

    [Fact]
    public void Session_list_optimization_badge_warns_when_default_comparison_is_mixed()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var compare = seed.Compare(JournalSeed.Session("cli:mixed-badge"), "orders", "whole", "h0", "h1");
        var conditions = JournalSeed.ReproConditions() with
        {
            Cases =
            [
                new ConditionsCase("0", 0, "@id = 1", null, [], "fingerprint-0"),
                new ConditionsCase("1", 1, "@id = 2", null, [], "fingerprint-1"),
            ],
        };
        var metrics = new BenchmarkJournalRecord(
        [
            OptimizationVariant("baseline", 100, matrixCell: 0),
            OptimizationVariant("candidate", 80, matrixCell: 0),
            OptimizationVariant("baseline", 100, matrixCell: 1),
            OptimizationVariant("candidate", 140, matrixCell: 1),
        ], []);
        seed.CompareEvidence(compare, conditions,
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":true}},{"caseId":"1","caseLabel":"@id = 2","equivalence":{"equivalent":true}}]}""",
            metrics);

        var badge = Assert.Single(Assert.Single(Reader(home).Sessions(new SessionQuery()).Items).Optimization);

        Assert.True(badge.Warning);
        Assert.Equal("+10%", badge.Change);
    }

    [Fact]
    public void Compare_operation_summaries_include_subject_pair_and_reads()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var compare = seed.Compare(JournalSeed.Session("cli:operation-summary"), "orders", "whole", "h0", "h1");
        seed.CompareEvidence(compare, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":true}}]}""",
            OptimizationMetrics(100, 20));

        var summary = Assert.Single(Reader(home).Operations(new OperationQuery()).Items);
        Assert.Equal("orders", summary.Subject);
        Assert.Equal("v0 → v1", summary.VersionPair);
        Assert.Equal(100, summary.ReadsBefore);
        Assert.Equal(20, summary.ReadsAfter);
    }

    [Fact]
    public void Over_granted_requires_one_megabyte_and_less_than_one_quarter_used()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:small-grant"), benchmark: GrantBenchmark(512, 10));
        seed.Operation(JournalSeed.Session("cli:large-grant"), benchmark: GrantBenchmark(4096, 100));

        var items = Reader(home).Operations(new OperationQuery()).Items.ToDictionary(item => item.SessionId);
        Assert.False(items[1].OverGranted);
        Assert.True(items[2].OverGranted);
    }

    private static BenchmarkJournalRecord OptimizationMetrics(long before, long after) => new(
    [OptimizationVariant("baseline", before), OptimizationVariant("candidate", after)], []);

    private static JournalVariantMetrics OptimizationVariant(string variant, long reads, int? matrixCell = null) => new(
        variant, null, matrixCell, 1, new CompareDistribution(10, 10, 10), new CompareDistribution(8, 8, 8),
        new CompareDistribution(reads, reads, reads), null, null, null, null, null, null, 0, false, false, 0,
        [], null, [new JournalTableIo("Orders", reads, 1, 0, null, null, 0, null, null, 0)], [])
    {
        CaseId = "0",
        CaseLabel = "@id = 1",
    };

    private static BenchmarkJournalRecord GrantBenchmark(long granted, long used) => new(
    [new JournalVariantMetrics("measure", null, null, 1, null, null, null, null, granted, used, null, null, null,
        0, false, false, 0, [], null, [], [])], []);

    private static JournalVariantMetrics OptimizationMetric(string variant, long logicalReads, long elapsed, long scans) =>
        new(variant, null, null, 3, new CompareDistribution(elapsed, elapsed, elapsed), new CompareDistribution(elapsed, elapsed, elapsed),
            new CompareDistribution(logicalReads, logicalReads, logicalReads), null, null, null, null, null, null, 0, false, false, 0,
            [], null, [new JournalTableIo("Orders", logicalReads - 10, scans, 0, null, null, 10, null, null, 0)], [])
        {
            CaseId = "0",
            CaseLabel = "@CustomerId = 1",
        };

    [Fact]
    public void Session_and_operation_carry_title_roots_and_cancel_reason()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("mcp:r") with
        {
            ClientTitle = "Claude Code",
            RootsJson = """[{"uri":"file:///w","name":"w"},{"uri":"file:///x"}]""",
        };
        seed.Operation(session, status: "failed", exitCode: -1, errorKind: "cancelled", cancelReason: "client");

        var reader = Reader(home);
        var id = Assert.Single(reader.Sessions(new SessionQuery()).Items).Id;
        var detail = reader.Session(id)!;

        Assert.Equal("Claude Code", detail.Session.ClientTitle);
        Assert.Equal([new SessionRoot("file:///w", "w"), new SessionRoot("file:///x", null)], detail.Session.Roots);
        Assert.Equal("client", Assert.Single(detail.Operations).CancelReason);
    }

    [Fact]
    public void Compare_pages_have_no_gaps_and_versions_use_all_retained_history_before_paging()
    {
        using var home = new TempHome();
        // More rows than JournalReader.CompareHistoryPageLimit (100) so the history
        // has to be read across an internal page boundary, and more than one user page
        // at the limit below, so the cursor has to advance. That is the property; the
        // session operation cap is a different test.
        const int attempts = 137;
        JournalSeed.Compares(home.DatabasePath, "cli:compare-history", attempts);

        var reader = Reader(home);
        var sessionId = Assert.Single(reader.Sessions(new SessionQuery()).Items).Id;
        var allIds = new HashSet<long>();
        long? cursor = null;
        Page<CompareAttemptSummary> page;
        do
        {
            page = reader.CompareAttempts(sessionId, cursor, 20);
            Assert.NotEmpty(page.Items);
            Assert.All(page.Items, item => Assert.True(allIds.Add(item.OperationId)));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(attempts, allIds.Count);
        var latest = reader.CompareAttempts(sessionId, limit: 1).Items.Single();
        Assert.Equal("v0", latest.BaselineVersion);
        Assert.Equal($"v{attempts}", latest.CandidateVersion);
        Assert.Equal(allIds.Max(), Assert.Single(reader.Session(sessionId)!.CompareSubjects).LatestWhole!.OperationId);
    }

    [Fact]
    public void Session_operations_are_capped_and_keep_the_newest()
    {
        using var home = new TempHome();
        // The cap is a LIMIT in the reader's SQL, so the filler rows are inserted
        // directly: writing them through the journal API costs a transaction with an
        // fsync each and made this test 42 seconds for the same 501 rows. The session
        // itself is created through the API, so the row stays schema-correct.
        const int seeded = JournalReader.SessionOperationLimit + 1;
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:operation-cap"));
        using (var connection = OpenSeedDatabase(home.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            for (var index = 2; index <= seeded; index++)
            {
                command.CommandText = $"""
                    INSERT INTO operations (id, session_id, operation, host_pid, started_at, updated_at, finished_at, status)
                    VALUES ({index}, 1, 'query', 5151, '2026-10-07T08:00:00.000Z', '2026-10-07T08:00:01.000Z', '2026-10-07T08:00:01.000Z', 'succeeded');
                    """;
                command.ExecuteNonQuery();
            }
        }

        var reader = Reader(home);
        var sessionId = Assert.Single(reader.Sessions(new SessionQuery()).Items).Id;

        var operations = reader.Session(sessionId)!.Operations;
        Assert.Equal(JournalReader.SessionOperationLimit, operations.Count);
        // Newest first, and only the oldest row (id 1) falls outside the cap.
        Assert.Equal(seeded, operations[0].Id);
        Assert.Equal(2, operations[^1].Id);
    }

    [Fact]
    public void Legacy_compare_without_context_is_counted_as_unassigned_and_has_no_version()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Compare(JournalSeed.Session("cli:legacy-compare"), null, null, "h1", "h2");
        var reader = Reader(home);
        var sessionId = Assert.Single(reader.Sessions(new SessionQuery()).Items).Id;

        var detail = reader.Session(sessionId)!;
        var attempt = Assert.Single(reader.CompareAttempts(sessionId).Items);

        Assert.Empty(detail.CompareSubjects);
        Assert.Equal(1, detail.UnassignedCompareCount);
        Assert.Null(attempt.Subject);
        Assert.Null(attempt.BaselineVersion);
        Assert.Null(attempt.CandidateVersion);
    }

    [Fact]
    public void Compare_page_and_operation_detail_hydrate_evidence_only_for_the_selected_attempt()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:evidence-page");
        var old = seed.Compare(session, "orders", "whole", "h0", "h1");
        var latest = seed.Compare(session, "orders", "whole", "h1", "h2");
        CorruptCompareEvidence(home.DatabasePath, old.OperationId);
        var hydratedIds = new List<long>();
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses())
        {
            CompareEvidenceQueryObserver = ids => hydratedIds.AddRange(ids),
        };
        var sessionId = Assert.Single(reader.Sessions(new SessionQuery()).Items).Id;

        var page = reader.CompareAttempts(sessionId, limit: 1);
        var detail = reader.Operation(latest.OperationId)!;

        Assert.Equal(latest.OperationId, Assert.Single(page.Items).OperationId);
        Assert.Null(Assert.Single(page.Items).Summary);
        Assert.Equal("h1", detail.Operation.SqlHash);
        Assert.Equal("orders", detail.CompareSubject);
        Assert.DoesNotContain(old.OperationId, hydratedIds);
        Assert.Contains(latest.OperationId, hydratedIds);

        hydratedIds.Clear();
        var sessionDetail = reader.Session(sessionId)!;
        Assert.Equal(latest.OperationId, Assert.Single(sessionDetail.CompareSubjects).LatestWhole!.OperationId);
        Assert.Equal([latest.OperationId], hydratedIds);
    }

    [Fact]
    public void Noncompare_operation_detail_does_not_scan_malformed_compare_metadata()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:no-compare-scan");
        seed.Compare(session, "orders", "whole", "h0", "h1");
        var query = seed.Operation(session);
        using (var connection = OpenSeedDatabase(home.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE operations SET host_pid = X'010203' WHERE operation = 'compare';";
            command.ExecuteNonQuery();
        }

        var hydratedIds = new List<long>();
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses())
        {
            CompareEvidenceQueryObserver = ids => hydratedIds.AddRange(ids),
        };
        var detail = reader.Operation(query.OperationId);

        Assert.NotNull(detail);
        Assert.Equal("query", detail.Operation.Operation);
        Assert.Empty(hydratedIds);
    }

    [Fact]
    public void Operation_detail_includes_phase_aggregates_and_events_but_omits_events_for_legacy_rows()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:phase-detail");
        var timed = seed.Operation(session, durationMs: 100);
        var legacy = seed.Operation(session, durationMs: 200, complete: false);
        using (var connection = OpenSeedDatabase(home.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO operation_phases (operation_id, phase, count, total_ms, max_ms)
                VALUES ($id, 'execute', 2, 30, 20);
                INSERT INTO operation_events (operation_id, seq, offset_ms, phase, duration_ms, outcome, error_number, detail_json)
                VALUES ($id, 1, 12, 'connect', 8, 'failed', 18456, '{"clientConnectionId":"550e8400-e29b-41d4-a716-446655440000"}');
                INSERT INTO operation_events (operation_id, seq, offset_ms, phase, duration_ms, outcome, error_number, detail_json)
                VALUES ($id, 2, 20, 'other', 0, 'truncated', NULL, '{"dropped":3}');
                """;
            command.Parameters.AddWithValue("$id", timed.OperationId);
            command.ExecuteNonQuery();
        }

        var reader = Reader(home);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using var timedJson = JsonDocument.Parse(JsonSerializer.Serialize(reader.Operation(timed.OperationId), options));
        using var legacyJson = JsonDocument.Parse(JsonSerializer.Serialize(reader.Operation(legacy.OperationId), options));

        var phase = timedJson.RootElement.GetProperty("phases").EnumerateArray()
            .Single(value => value.GetProperty("phase").GetString() == "execute");
        Assert.Equal(2, phase.GetProperty("count").GetInt32());
        Assert.Equal(30, phase.GetProperty("totalMs").GetInt64());
        Assert.Equal(20, phase.GetProperty("maxMs").GetInt64());
        var events = timedJson.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(12, events[0].GetProperty("offsetMs").GetInt64());
        Assert.Equal(8, events[0].GetProperty("durationMs").GetInt64());
        Assert.Equal("failed", events[0].GetProperty("outcome").GetString());
        Assert.Equal(18456, events[0].GetProperty("errorNumber").GetInt32());
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", events[0].GetProperty("clientConnectionId").GetString());
        Assert.Equal("truncated", events[1].GetProperty("outcome").GetString());
        Assert.Equal(3, events[1].GetProperty("droppedCount").GetInt32());
        Assert.Empty(legacyJson.RootElement.GetProperty("phases").EnumerateArray());
        Assert.False(legacyJson.RootElement.TryGetProperty("events", out _));
    }

    [Fact]
    public void Operation_detail_reads_a_v9_database_without_timing_tables()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var handle = seed.Operation(JournalSeed.Session("cli:v9"));
        using (var connection = OpenSeedDatabase(home.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE operation_events; DROP TABLE operation_phases; PRAGMA user_version = 9;";
            command.ExecuteNonQuery();
        }

        var detail = Reader(home).Operation(handle.OperationId);

        Assert.NotNull(detail);
        Assert.Empty(detail.Phases!);
        Assert.Null(detail.Events);
    }

    private static void CorruptCompareEvidence(string databasePath, long operationId)
    {
        using var connection = OpenSeedDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE operations SET summary_json = X'010203', conditions_json = X'010203' WHERE id = $id;";
        command.Parameters.AddWithValue("$id", operationId);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenSeedDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }

    [Fact]
    public void Operation_carries_the_agent_call_metadata()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("mcp:a");
        seed.Operation(session, agent: new AgentCallMetadata("toolu_1", "m", "low", "s", "t", "exec", "user"));
        seed.Operation(session);

        var reader = Reader(home);
        var items = reader.Operations(new OperationQuery()).Items;
        var labelled = items.Single(o => o.AgentModel is not null);
        var plain = items.Single(o => o.AgentModel is null);

        Assert.Equal("low", labelled.AgentReasoningEffort);
        Assert.Equal(new AgentCall("toolu_1", "m", "low", "s", "t", "exec", "user"), reader.Operation(labelled.Id)!.Agent);
        Assert.Null(reader.Operation(plain.Id)!.Agent);
    }

    [Fact]
    public void Session_carries_the_protocol_version()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("mcp:p") with { ProtocolVersion = "2025-06-18" });

        var reader = Reader(home);
        var id = Assert.Single(reader.Sessions(new SessionQuery()).Items).Id;

        Assert.Equal("2025-06-18", reader.Session(id)!.Session.ProtocolVersion);
    }

    [Fact]
    public void Operation_detail_reads_sensitive_setup_sql()
    {
        using var home = new TempHome();
        var setup = "CREATE TABLE #data (id int);";
        var handle = new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:setup"), operation: "measure", sql: "SELECT id FROM #data", setupSql: setup);

        var detail = Reader(home).Operation(handle.OperationId)!;

        Assert.Equal("SELECT id FROM #data", detail.SqlText);
        Assert.Equal(setup, detail.SetupSqlText);
    }

    // The reader never migrates (the dashboard host opens the journal first and refuses an older
    // schema), so "unknown" is a v6 row whose metadata columns were never written.
    [Fact]
    public void Rows_without_recorded_metadata_read_as_null()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:old"));

        var reader = Reader(home);
        var detail = reader.Session(Assert.Single(reader.Sessions(new SessionQuery()).Items).Id)!;

        Assert.Null(detail.Session.ClientTitle);
        Assert.Null(detail.Session.Roots);
        Assert.Null(detail.Session.ProtocolVersion);
        Assert.Null(Assert.Single(detail.Operations).CancelReason);
    }

    [Fact]
    public void Empty_and_malformed_roots_are_distinct()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("mcp:empty") with { RootsJson = "[]" });
        seed.Operation(JournalSeed.Session("mcp:bad") with { RootsJson = "not json" });

        var sessions = Reader(home).Sessions(new SessionQuery()).Items.ToDictionary(s => s.SessionKey);

        Assert.Empty(sessions["mcp:empty"].Roots!);
        Assert.Null(sessions["mcp:bad"].Roots);
    }

    [Fact]
    public void Sessions_are_newest_first_with_counts_and_keyset_paging()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var a = JournalSeed.Session("cli:a", "claude");
        var b = JournalSeed.Session("cli:b", "codex");
        seed.Operation(a);
        seed.Operation(a, status: "failed", exitCode: 5);
        seed.Operation(b, status: "rejected", exitCode: 2);

        var reader = Reader(home);
        var first = reader.Sessions(new SessionQuery(Limit: 1, Starred: false));
        var second = reader.Sessions(new SessionQuery(Limit: 1, Starred: false, Cursor: first.NextCursor));

        Assert.Equal("codex", Assert.Single(first.Items).AgentKind);
        Assert.Equal(1, first.Items[0].Rejected);
        var claude = Assert.Single(second.Items);
        Assert.Equal((2, 1), (claude.Operations, claude.Failed));
        Assert.Null(second.NextCursor);
        Assert.Equal("claude", Assert.Single(reader.Sessions(new SessionQuery(Agent: "claude")).Items).AgentKind);
        var unfiltered = reader.Sessions(new SessionQuery(Limit: 1));
        Assert.Single(unfiltered.Items);
        Assert.Null(unfiltered.NextCursor);
    }

    [Fact]
    public void Dead_running_operations_are_reported_abandoned()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:live", hostPid: 100), complete: false);
        seed.Operation(JournalSeed.Session("cli:dead", hostPid: 200), complete: false);
        var processes = new FakeProcesses().Alive(100, JournalSeed.HostStarted.AddMilliseconds(300));
        var reader = Reader(home, processes);

        var statuses = reader.Operations(new OperationQuery()).Items.ToDictionary(o => o.SessionId, o => o.Status);

        Assert.Contains("running", statuses.Values);
        Assert.Contains("abandoned", statuses.Values);
        Assert.Single(reader.Operations(new OperationQuery(Status: "abandoned")).Items);
        Assert.Single(reader.Operations(new OperationQuery(Status: "running")).Items);
        var stats = reader.Stats(new StatsQuery());
        Assert.Equal(1, stats.Statuses.Single(s => s.Key == "abandoned").Count);
        Assert.Equal(1, stats.Statuses.Single(s => s.Key == "running").Count);
    }

    [Fact]
    public void Reused_pid_with_a_different_start_is_abandoned()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:x", hostPid: 100), complete: false);

        var reader = Reader(home, new FakeProcesses().Alive(100, JournalSeed.HostStarted.AddHours(2)));

        Assert.Equal("abandoned", Assert.Single(reader.Operations(new OperationQuery()).Items).Status);
    }

    [Fact]
    public void Dead_hosts_are_looked_up_once_while_live_hosts_are_rechecked()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:live", hostPid: 100), complete: false);
        seed.Operation(JournalSeed.Session("cli:dead", hostPid: 200), complete: false);
        var processes = new FakeProcesses().Alive(100, JournalSeed.HostStarted);
        var reader = Reader(home, processes);

        for (var tick = 0; tick < 3; tick++)
        {
            using var connection = reader.OpenReadOnly()!;
            var statuses = reader.RunningOperations(connection).ToDictionary(o => o.SessionId, o => o.Status);
            Assert.Equal(["abandoned", "running"], statuses.Values.Order());
        }

        Assert.Equal("abandoned", reader.Operations(new OperationQuery(Status: "abandoned")).Items.Single().Status);
        Assert.Equal(1, processes.Lookups(200));
        Assert.Equal(4, processes.Lookups(100));
    }

    [Fact]
    public void Dead_process_cache_starts_over_when_full()
    {
        var dead = new DeadProcesses();
        for (var pid = 0; pid < DeadProcesses.Capacity; pid++)
            dead.Add(pid, null);
        Assert.Equal(DeadProcesses.Capacity, dead.Count);

        dead.Add(-1, "x");

        Assert.Equal(1, dead.Count);
        Assert.True(dead.Contains(-1, "x"));
        Assert.False(dead.Contains(0, null));
    }

    [ProcessReaderFact]
    public void Real_exited_host_is_abandoned_while_the_current_process_stays_running()
    {
        using var home = new TempHome();
        // A child that waits on stdin, so its snapshot is taken while it is certainly alive.
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "cat")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        ProcessSnapshot child;
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            child = ProcessInfo.Current.Get(process.Id)!;
            Assert.NotNull(child);
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10_000));
        }

        var self = ProcessInfo.Current.Get(Environment.ProcessId)!;
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(Host("cli:exited", child), complete: false);
        seed.Operation(Host("cli:self", self), complete: false);
        var reader = new JournalReader(home.DatabasePath, ProcessInfo.Current);

        var statuses = reader.Operations(new OperationQuery()).Items.ToDictionary(o => o.SessionId, o => o.Status);
        var sessions = reader.Sessions(new SessionQuery()).Items.ToDictionary(s => s.SessionKey, s => s.Id);

        Assert.Equal("abandoned", statuses[sessions["cli:exited"]]);
        Assert.Equal("running", statuses[sessions["cli:self"]]);

        static SessionIdentity Host(string key, ProcessSnapshot host) =>
            JournalSeed.Session(key, hostPid: host.Pid) with { HostStartedAt = host.StartedAt };
    }

    [Fact]
    public void Live_filtered_pages_scan_past_dead_running_rows()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:live", hostPid: 100), complete: false);
        for (var i = 0; i < 3; i++)
            seed.Operation(JournalSeed.Session("cli:dead", hostPid: 200), complete: false);
        var reader = Reader(home, new FakeProcesses().Alive(100, JournalSeed.HostStarted));

        var running = reader.Operations(new OperationQuery(Status: "running", Limit: 2));
        var abandoned = reader.Operations(new OperationQuery(Status: "abandoned", Limit: 2));
        var rest = reader.Operations(new OperationQuery(Status: "abandoned", Limit: 2, Cursor: abandoned.NextCursor));

        Assert.Equal("running", Assert.Single(running.Items).Status);
        Assert.Null(running.NextCursor);
        Assert.Equal(2, abandoned.Items.Count);
        Assert.NotNull(abandoned.NextCursor);
        Assert.Equal("abandoned", Assert.Single(rest.Items).Status);
        Assert.Null(rest.NextCursor);
    }

    [Fact]
    public void Session_detail_lists_operations_with_running_and_abandoned_counts()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:x", hostPid: 100), complete: false);
        seed.Operation(JournalSeed.Session("cli:x", hostPid: 200), complete: false);
        seed.Operation(JournalSeed.Session("cli:x", hostPid: 100));
        var reader = Reader(home, new FakeProcesses().Alive(100, JournalSeed.HostStarted));
        var summary = Assert.Single(reader.Sessions(new SessionQuery()).Items);

        var detail = reader.Session(summary.Id)!;

        Assert.Equal((1, 1), (summary.Running, summary.Abandoned));
        Assert.Equal((1, 1), (detail.Session.Running, detail.Session.Abandoned));
        Assert.Equal(3, detail.Session.Operations);
        Assert.Equal(["succeeded", "abandoned", "running"], detail.Operations.Select(o => o.Status));
        Assert.Null(reader.Session(summary.Id + 1));
    }

    [Fact]
    public void Malformed_stored_json_reads_as_null_fields()
    {
        using var home = new TempHome();
        var handle = new JournalSeed(home.DatabasePath).Operation(
            JournalSeed.Session("cli:j"), operation: "measure", benchmark: JournalSeed.Benchmark());
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE operations SET vars_json = '{', summary_json = 'nope', progress_json = '[';
                UPDATE operation_metrics SET waits_json = '[{"waitType":';
                """;
            command.ExecuteNonQuery();
        }

        var reader = Reader(home);
        var detail = reader.Operation(handle.OperationId)!;
        var stats = reader.Stats(new StatsQuery());

        Assert.Null(detail.Vars);
        Assert.Null(detail.Summary);
        Assert.Null(detail.Operation.Progress);
        Assert.Null(Assert.Single(detail.Variants).Waits);
        Assert.Empty(stats.TopWaits);
    }

    [Fact]
    public void Operation_detail_has_metrics_table_io_waits_and_plan_links()
    {
        using var home = new TempHome();
        var handle = new JournalSeed(home.DatabasePath).Operation(
            JournalSeed.Session("cli:m"), operation: "measure", benchmark: JournalSeed.Benchmark(readsMedian: 50, physical: 3, granted: 8192, used: 512));

        var detail = Reader(home).Operation(handle.OperationId)!;

        Assert.Equal("measure", detail.Operation.Operation);
        Assert.Equal("acme", detail.Vars!["tenant"]);
        Assert.Null(detail.SqlText);
        var variant = Assert.Single(detail.Variants);
        Assert.Equal(new Spread(50, 50, 50), variant.LogicalReads);
        Assert.Equal(8192, variant.GrantGrantedKb);
        Assert.Equal("PAGEIOLATCH_SH", variant.Waits!.Value[0].GetProperty("waitType").GetString());
        var table = Assert.Single(variant.TableIo);
        Assert.Equal(("Orders", 3L, 1), (table.Table, table.PhysicalReads, table.ColdRuns));
        var plan = Assert.Single(variant.Plans);
        Assert.False(plan.Stored);
        Assert.Equal(50, detail.Operation.LogicalReadsMedian);
        Assert.True(detail.Operation.HasSpill);
        Assert.True(detail.Operation.ColdCache);
        Assert.True(detail.Operation.OverGranted);
    }

    [Fact]
    public void Operation_detail_exposes_canonical_conditions_case_labels_and_total_reads()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var benchmark = JournalSeed.Benchmark(readsMedian: 50) with
        {
            Variants = [JournalSeed.Benchmark(readsMedian: 50).Variants[0] with { CaseId = "tenant-a", CaseLabel = "@Tenant = N'A'" }],
        };
        var handle = seed.Operation(JournalSeed.Session("cli:case-contract"), operation: "measure", benchmark: benchmark);
        seed.RecordConditions(handle, JournalSeed.ReproConditions() with
        {
            Cases = [new ConditionsCase("tenant-a", 0, "@Tenant = N'A'", null, [], "fingerprint")],
        });

        var detail = Reader(home).Operation(handle.OperationId)!;
        var variant = Assert.Single(detail.Variants);
        var table = Assert.Single(variant.TableIo);

        Assert.Equal("tenant-a", variant.CaseId);
        Assert.Equal("@Tenant = N'A'", variant.CaseLabel);
        Assert.Equal(50, variant.TotalLogicalReadsMedian);
        Assert.Equal("median of run totals (data + LOB)", variant.TotalLogicalReadsLabel);
        Assert.Equal(50, table.TotalLogicalReads);
        Assert.Equal(JsonValueKind.Object, detail.Conditions!.Value.ValueKind);
        Assert.Equal("sqlserver", detail.Conditions.Value.GetProperty("engine").GetString());
    }

    [Fact]
    public void Sensitive_journal_exposes_sql_text_and_stored_plans()
    {
        using var home = new TempHome();
        var handle = new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:s"), operation: "measure", sql: "SELECT 42", benchmark: JournalSeed.Benchmark());
        var reader = Reader(home);

        var detail = reader.Operation(handle.OperationId)!;
        var stored = reader.Plan(new string('A', 64))!;

        Assert.Equal("SELECT 42", detail.SqlText);
        Assert.True(Assert.Single(Assert.Single(detail.Variants).Plans).Stored);
        Assert.Equal("showplan-xml", stored.Format);
        Assert.Equal(Journal.PlanMetricsExtractorTests.ActualPlan, stored.Document);
        Assert.Null(reader.Plan(new string('B', 64)));
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("schema")]
    [InlineData("counts")]
    [InlineData("space")]
    [InlineData("qstop")]
    [InlineData("indexes")]
    public void Historical_rows_of_removed_commands_still_load(string removedOperation)
    {
        // Plan 060 removed these commands; journal rows written by earlier versions stay readable.
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:old");
        seed.Operation(session, operation: removedOperation);
        var reader = Reader(home);
        var sessionId = reader.Sessions(new SessionQuery()).Items.Single().Id;

        var page = reader.Operations(new OperationQuery(SessionId: sessionId, Operation: removedOperation));

        var row = Assert.Single(page.Items);
        Assert.Equal(removedOperation, row.Operation);
        Assert.Equal(removedOperation, reader.Operation(row.Id)!.Operation.Operation);
    }

    [Fact]
    public void Operations_filter_by_session_status_and_operation_and_page()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var a = JournalSeed.Session("cli:a");
        for (var i = 0; i < 5; i++)
            seed.Operation(a, operation: i % 2 == 0 ? "query" : "ping");
        var reader = Reader(home);
        var sessionId = reader.Sessions(new SessionQuery()).Items.Single().Id;

        var page = reader.Operations(new OperationQuery(SessionId: sessionId, Operation: "query", Limit: 2));
        var rest = reader.Operations(new OperationQuery(SessionId: sessionId, Operation: "query", Limit: 2, Cursor: page.NextCursor));

        Assert.Equal(2, page.Items.Count);
        Assert.Single(rest.Items);
        Assert.True(page.Items[0].Id > page.Items[1].Id);
        Assert.All(page.Items.Concat(rest.Items), o => Assert.Equal("query", o.Operation));
    }

    [Fact]
    public void Stats_aggregate_days_statuses_sql_tables_waits_tokens_and_targets()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var s = JournalSeed.Session("cli:a");
        seed.Operation(s, sql: "SELECT 1", durationMs: 10);
        seed.Operation(s, sql: "SELECT 1", durationMs: 30);
        seed.Operation(s, operation: "measure", sql: "SELECT 2", durationMs: 100, benchmark: JournalSeed.Benchmark(readsMedian: 70, physical: 2));
        seed.Operation(s, status: "rejected", exitCode: 2, sql: "DELETE x");

        var stats = Reader(home).Stats(new StatsQuery());

        Assert.Equal(4, Assert.Single(stats.OperationsPerDay).Count);
        Assert.Equal("2026-10-07", stats.OperationsPerDay[0].Day);
        Assert.Equal(1, stats.Statuses.Single(x => x.Key == "rejected").Count);
        Assert.Equal(1, stats.ExitCodes.Single(x => x.Key == "2").Count);
        var top = stats.TopSqlByCount[0];
        Assert.Equal((OperationJournalDescriber.SqlHash("SELECT 1"), 2, 40L), (top.SqlHash, top.Count, top.TotalDurationMs));
        Assert.Equal(OperationJournalDescriber.SqlHash("SELECT 2"), stats.TopSqlByDuration[0].SqlHash);
        Assert.Equal(("Orders", 210L), (stats.TopTablesByLogicalReads[0].Table, stats.TopTablesByLogicalReads[0].LogicalReads));
        Assert.Equal(("PAGEIOLATCH_SH", 120d), (stats.TopWaits[0].WaitType, stats.TopWaits[0].TotalWaitMs));
        Assert.Equal(1, stats.SpillOperations);
        Assert.Equal(1, stats.ColdCacheOperations);
        Assert.Equal((160L, 40L), (Assert.Single(stats.WorkflowCosts).OutputBytes,
            stats.WorkflowCosts[0].EstimatedOutputTokens));
        Assert.Equal(("local", "db", 4), (stats.Targets[0].Profile, stats.Targets[0].Database, stats.Targets[0].Count));
    }

    [Fact]
    public void Legacy_output_cost_uses_measured_emitted_bytes_without_comparison()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var s = JournalSeed.Session("cli:a");
        seed.Operation(s);
        seed.Operation(s, tokens: SeedTokens.RawOnly);
        seed.Operation(s, tokens: SeedTokens.EmittedOnly);
        seed.Operation(s, tokens: SeedTokens.EmittedOnly);

        var stats = Reader(home).Stats(new StatsQuery());

        var legacy = Assert.Single(stats.WorkflowCosts);
        Assert.Equal("legacy-artifact-volume", legacy.AccountingKind);
        Assert.Equal((120L, 30L, 3), (legacy.OutputBytes, legacy.EstimatedOutputTokens, legacy.CallCount));
        Assert.Null(legacy.Comparison);
    }

    [Fact]
    public void Stats_respect_the_time_window()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:a"));

        var stats = Reader(home).Stats(new StatsQuery(From: new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero)));

        Assert.Empty(stats.OperationsPerDay);
    }

    [Fact]
    public void Workflow_cost_adds_root_and_exactly_linked_artifact_outputs_and_uses_root_baseline()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:workflow");
        var root = seed.Operation(session);
        var read1 = seed.Operation(session, operation: "artifact");
        var read2 = seed.Operation(session, operation: "artifact");
        SetFootprint(home.DatabasePath, root, operationRef: "root-1", parentRef: null,
            rawBytes: 4000, emittedBytes: 1600, rawTokens: 1000, emittedTokens: 400);
        SetFootprint(home.DatabasePath, read1, operationRef: "read-1", parentRef: "root-1",
            rawBytes: 1200, emittedBytes: 1200, rawTokens: 300, emittedTokens: 300);
        SetFootprint(home.DatabasePath, read2, operationRef: "read-2", parentRef: "root-1",
            rawBytes: 1200, emittedBytes: 1200, rawTokens: 300, emittedTokens: 300);

        var workflow = Assert.Single(Reader(home).Stats(new StatsQuery()).WorkflowCosts);

        Assert.Equal(("root-1", 4000L, 1000L, 3),
            (workflow.RootOperationRef, workflow.OutputBytes, workflow.EstimatedOutputTokens, workflow.CallCount));
        Assert.Equal("utf8-bytes-div-4", workflow.TokenEstimationMethod);
        Assert.Equal("single-measured-execution-output", workflow.Comparison!.BaselineKind);
        Assert.Equal((4000L, 1000L, 0L, 0L),
            (workflow.Comparison.BaselineBytes, workflow.Comparison.BaselineEstimatedTokens,
                workflow.Comparison.OutputOverheadBytes, workflow.Comparison.EstimatedOutputTokenDelta));
        Assert.Equal("unmeasured", workflow.Coverage.Input);
        Assert.Equal("unmeasured", workflow.Coverage.Discovery);
        Assert.Equal("unmeasured", workflow.Coverage.Chat);
    }

    [Fact]
    public void Workflow_cost_uses_producer_baseline_outside_window_and_reports_positive_output_overhead()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:window");
        var root = seed.Operation(session);
        var read = seed.Operation(session, operation: "artifact");
        SetFootprint(home.DatabasePath, root, operationRef: "root-2", parentRef: null,
            rawBytes: 4000, emittedBytes: 2000, rawTokens: 1000, emittedTokens: 500);
        SetFootprint(home.DatabasePath, read, operationRef: "read-3", parentRef: "root-2",
            rawBytes: 1000, emittedBytes: 5000, rawTokens: 250, emittedTokens: 1250);

        var workflow = Assert.Single(Reader(home).Stats(new StatsQuery(
            From: new DateTimeOffset(2026, 10, 7, 9, 0, 1, TimeSpan.Zero))).WorkflowCosts);

        Assert.Equal(("root-2", 5000L, 1250L, 1),
            (workflow.RootOperationRef, workflow.OutputBytes, workflow.EstimatedOutputTokens, workflow.CallCount));
        Assert.Equal((4000L, 1000L, 1000L, -250L),
            (workflow.Comparison!.BaselineBytes, workflow.Comparison.BaselineEstimatedTokens,
                workflow.Comparison.OutputOverheadBytes, workflow.Comparison.EstimatedOutputTokenDelta));
    }

    [Fact]
    public void Workflow_cost_keeps_missing_baselines_and_legacy_volume_out_of_comparisons()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:mixed");
        var noBaseline = seed.Operation(session);
        var legacy = seed.Operation(session);
        SetFootprint(home.DatabasePath, noBaseline, operationRef: "root-no-baseline", parentRef: null,
            rawBytes: null, emittedBytes: 800, rawTokens: null, emittedTokens: 200);
        SetFootprint(home.DatabasePath, legacy, operationRef: null, parentRef: null,
            rawBytes: 400, emittedBytes: 200, rawTokens: 100, emittedTokens: 50);

        var costs = Reader(home).Stats(new StatsQuery()).WorkflowCosts;

        Assert.Equal(2, costs.Count);
        var current = Assert.Single(costs, cost => cost.RootOperationRef == "root-no-baseline");
        Assert.Null(current.Comparison);
        var old = Assert.Single(costs, cost => cost.AccountingKind == "legacy-artifact-volume");
        Assert.Null(old.RootOperationRef);
        Assert.Null(old.Comparison);
        Assert.Equal((200L, 50L, 1), (old.OutputBytes, old.EstimatedOutputTokens, old.CallCount));
    }

    [Fact]
    public void Dry_run_is_in_output_cost_and_calls_but_not_database_execution_count()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:dry-run");
        var dryRun = seed.Operation(session);
        SetFootprint(home.DatabasePath, dryRun, "dry-run-root", null, 4000, 1600, 1000, 400, dryRun: true);

        var stats = Reader(home).Stats(new StatsQuery());
        var cost = Assert.Single(stats.WorkflowCosts);

        Assert.Equal((1600L, 400L, 1), (cost.OutputBytes, cost.EstimatedOutputTokens, cost.CallCount));
        Assert.Equal(1, cost.DryRunCalls);
        Assert.Equal(0, cost.DatabaseExecutionCalls);
        Assert.Null(cost.Comparison);
    }

    [Fact]
    public void Failed_root_and_artifact_read_raw_footprints_never_supply_a_comparison_baseline()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:failed-root");
        var failedRoot = seed.Operation(session, status: "failed", exitCode: 5);
        var artifactRoot = seed.Operation(session, operation: "artifact");
        SetFootprint(home.DatabasePath, failedRoot, "failed-root", null, 4000, 800, 1000, 200);
        SetFootprint(home.DatabasePath, artifactRoot, "artifact-root", null, 4000, 800, 1000, 200);

        var costs = Reader(home).Stats(new StatsQuery()).WorkflowCosts;

        Assert.All(costs, cost => Assert.Null(cost.Comparison));
    }

    [Fact]
    public void Failed_linked_operation_without_emitted_bytes_disables_workflow_comparison()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:failed-linked");
        var root = seed.Operation(session);
        var successfulRead = seed.Operation(session, operation: "artifact");
        var failedRead = seed.Operation(session, operation: "artifact", status: "failed", exitCode: 5);
        SetFootprint(home.DatabasePath, root, "root-failed-linked", null, 4000, 1600, 1000, 400);
        SetFootprint(home.DatabasePath, successfulRead, "read-success", "root-failed-linked", 1200, 1200, 300, 300);
        SetFootprint(home.DatabasePath, failedRead, "read-failed", "root-failed-linked", null, null, null, null);

        var workflow = Assert.Single(Reader(home).Stats(new StatsQuery()).WorkflowCosts);

        Assert.Equal((2800L, 700L, 2), (workflow.OutputBytes, workflow.EstimatedOutputTokens, workflow.CallCount));
        Assert.Null(workflow.Comparison);
    }

    private static void SetFootprint(string path, JournalHandle handle, string? operationRef, string? parentRef,
        long? rawBytes, long? emittedBytes, long? rawTokens, long? emittedTokens, bool dryRun = false)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE operations SET operation_ref = $operationRef, parent_operation_ref = $parentRef, dry_run = $dryRun,
                raw_bytes = $rawBytes, emitted_bytes = $emittedBytes, raw_tokens = $rawTokens, emitted_tokens = $emittedTokens
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", handle.OperationId);
        command.Parameters.AddWithValue("$operationRef", (object?)operationRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentRef", (object?)parentRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$dryRun", dryRun ? 1 : 0);
        command.Parameters.AddWithValue("$rawBytes", (object?)rawBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$emittedBytes", (object?)emittedBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawTokens", (object?)rawTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$emittedTokens", (object?)emittedTokens ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Dimension_stats_are_complete_filtered_and_keep_missing_values_separate_from_unknown_text()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        for (var i = 0; i < 25; i++)
            seed.Operation(JournalSeed.Session($"cli:profile-{i}"), profile: $"profile-{i:D2}");
        seed.Operation(JournalSeed.Session("cli:literal"), profile: "app", variables: new Dictionary<string, string>
        {
            ["region"] = "Unknown",
            ["component"] = "worker",
        }, durationMs: 30);
        seed.Operation(JournalSeed.Session("cli:missing"), profile: "app", status: "failed", exitCode: 5,
            variables: new Dictionary<string, string> { ["component"] = "worker" }, durationMs: 20);
        seed.Operation(JournalSeed.Session("cli:running", hostPid: 100), profile: "app", complete: false,
            variables: new Dictionary<string, string> { ["component"] = "worker" });
        seed.Operation(JournalSeed.Session("cli:eu-api-1"), profile: "app",
            variables: new Dictionary<string, string> { ["region"] = "eu", ["component"] = "api" }, durationMs: 5);
        seed.Operation(JournalSeed.Session("cli:eu-api-2"), profile: "app",
            variables: new Dictionary<string, string> { ["region"] = "eu", ["component"] = "api" }, durationMs: 7);
        seed.Operation(JournalSeed.Session("cli:us-portal"), profile: "app", status: "rejected", exitCode: 2,
            variables: new Dictionary<string, string> { ["region"] = "us", ["component"] = "portal" }, durationMs: 11);
        seed.Operation(JournalSeed.Session("cli:deleted-profile"), profile: "deleted",
            variables: new Dictionary<string, string> { ["historical"] = "kept" });
        var definitions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["app"] = ["region", "component"],
            ["empty-profile"] = ["scope"],
        };

        var reader = Reader(home, new FakeProcesses());
        var all = reader.Stats(new StatsQuery(Profile: "app", RowDimension: "region", ColumnDimension: "component"), definitions);
        var filtered = reader.Stats(new StatsQuery(Profile: "app",
            Dimensions: new Dictionary<string, string?> { ["region"] = null },
            RowDimension: "region", ColumnDimension: "component"), definitions);
        var empty = reader.Stats(new StatsQuery(Profile: "empty-profile",
            To: new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero)), definitions);
        var deletedProfile = reader.Stats(new StatsQuery(Profile: "deleted"), definitions);

        Assert.Equal(27, all.ProfileOperations.Count);
        Assert.Equal(32, all.ProfileOperations.Sum(item => item.Operations));
        Assert.Equal("app", all.ProfileOperations[0].Profile);
        Assert.Equal("deleted", all.ProfileOperations[1].Profile);
        Assert.True(all.ProfileDimensions.ProfileDefinitionAvailable);
        Assert.Equal(6, all.ProfileDimensions.Operations);
        var region = Assert.Single(all.ProfileDimensions.Dimensions, item => item.Name == "region");
        Assert.Equal(4, region.Values.Count);
        var literal = Assert.Single(region.Values, item => !item.IsUnknown && item.Value == "Unknown");
        var missing = Assert.Single(region.Values, item => item.IsUnknown);
        Assert.Equal(("Unknown", 1), (literal.Value, literal.Operations));
        Assert.NotNull(literal.Percentage);
        Assert.Equal(100d / 6, literal.Percentage.Value, 3);
        Assert.Equal(("Unknown", 2, 20L, 1, 1, 1),
            (missing.Value, missing.Operations, missing.TotalDurationMs, missing.DurationAvailableOperations,
                missing.DurationUnavailableOperations, missing.Failed));
        var matrix = Assert.IsType<DimensionMatrixStats>(all.ProfileDimensions.Matrix);
        Assert.Equal(("region", "component", 6), (matrix.RowDimension, matrix.ColumnDimension, matrix.Totals.Operations));
        Assert.Equal(6, matrix.Cells.Sum(cell => cell.Metrics.Operations));
        Assert.Equal((1, 1), (matrix.Totals.Failed, matrix.Totals.Rejected));
        Assert.Equal(2, Assert.Single(matrix.Cells, cell => !cell.Row.IsUnknown && cell.Row.Value == "eu"
            && !cell.Column.IsUnknown && cell.Column.Value == "api").Metrics.Operations);
        var literalUnknown = Assert.Single(matrix.Cells, cell => !cell.Row.IsUnknown && cell.Row.Value == "Unknown"
            && cell.Column.Value == "worker");
        var missingRegion = Assert.Single(matrix.Cells, cell => cell.Row.IsUnknown && cell.Column.Value == "worker");
        Assert.Equal((1, 30L, 0), (literalUnknown.Metrics.Operations, literalUnknown.Metrics.TotalDurationMs,
            literalUnknown.Metrics.DurationUnavailableOperations));
        Assert.Equal((2, 20L, 1, 1), (missingRegion.Metrics.Operations, missingRegion.Metrics.TotalDurationMs,
            missingRegion.Metrics.DurationUnavailableOperations, missingRegion.Metrics.Failed));
        Assert.Equal(2, filtered.ProfileDimensions.Operations);
        Assert.Equal(2, filtered.ProfileDimensions.Matrix!.Totals.Operations);
        Assert.Equal(2, Assert.Single(filtered.ProfileDimensions.Matrix.Cells).Metrics.Operations);
        Assert.Equal(all.ProfileOperations, filtered.ProfileOperations);
        Assert.Equal(2, Assert.Single(filtered.ProfileDimensions.Dimensions, item => item.Name == "region").Values.Single().Operations);
        Assert.Equal(0, empty.ProfileDimensions.Operations);
        Assert.True(empty.ProfileDimensions.ProfileDefinitionAvailable);
        Assert.Empty(Assert.Single(empty.ProfileDimensions.Dimensions).Values);
        Assert.False(deletedProfile.ProfileDimensions.ProfileDefinitionAvailable);
        Assert.Equal("kept", Assert.Single(Assert.Single(deletedProfile.ProfileDimensions.Dimensions).Values).Value);
    }

    [Fact]
    public void Dimension_target_limit_reports_full_filtered_target_count_without_changing_totals()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        for (var i = 0; i < 23; i++)
            seed.Operation(JournalSeed.Session($"cli:selected-{i}"), profile: "app",
                variables: new Dictionary<string, string> { ["tenant"] = "selected", ["component"] = "api" },
                server: $"server-{i}", database: $"db-{i}");
        for (var i = 0; i < 3; i++)
            seed.Operation(JournalSeed.Session($"cli:other-{i}"), profile: "app",
                variables: new Dictionary<string, string> { ["tenant"] = "other", ["component"] = "api" },
                server: $"other-server-{i}", database: $"other-db-{i}");

        var stats = Reader(home).Stats(new StatsQuery(Profile: "app",
            Dimensions: new Dictionary<string, string?> { ["tenant"] = "selected" },
            RowDimension: "tenant", ColumnDimension: "component"),
            new Dictionary<string, IReadOnlyList<string>> { ["app"] = ["tenant", "component"] });

        Assert.Equal(23, stats.ProfileDimensions.Operations);
        Assert.Equal(23, stats.ProfileDimensions.TargetCount);
        Assert.Equal(20, stats.ProfileDimensions.Targets.Count);
        Assert.Equal(23, stats.ProfileDimensions.Matrix!.Totals.Operations);
        Assert.Equal(20, stats.ProfileDimensions.Targets.Sum(target => target.Count));
        Assert.Equal(20, stats.ProfileDimensions.Targets.Select(target => (target.Engine, target.Server, target.Database)).Distinct().Count());
    }

    [Fact]
    public void Operation_pages_filter_by_profile_and_multiple_dimensions_without_losing_cursor_matches()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:filters");
        for (var i = 0; i < 5; i++)
        {
            seed.Operation(session, profile: i % 2 == 0 ? "app" : "other", variables: new Dictionary<string, string>
            {
                ["region"] = i % 2 == 0 ? "eu:west" : "us",
                ["component"] = i < 3 ? "api" : "portal",
                ["tenant"] = "acme",
            });
        }

        var reader = Reader(home);
        var first = reader.Operations(new OperationQuery(Profile: "app", Dimensions: new Dictionary<string, string?>
        {
            ["region"] = "eu:west",
            ["component"] = "api",
            ["tenant"] = "acme",
        }, Limit: 1));
        var rest = reader.Operations(new OperationQuery(Profile: "app", Dimensions: new Dictionary<string, string?>
        {
            ["region"] = "eu:west",
            ["component"] = "api",
            ["tenant"] = "acme",
        }, Cursor: first.NextCursor, Limit: 1));

        Assert.NotNull(first.NextCursor);
        Assert.Null(rest.NextCursor);
        Assert.Equal(2, first.Items.Count + rest.Items.Count);
        Assert.All(first.Items.Concat(rest.Items), operation => Assert.Equal("app", operation.Profile));
    }

    [Fact]
    public void Unprofiled_scope_and_minute_bounds_match_statistics_and_drilldown()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:unprofiled");
        seed.Operation(session, profile: null, variables: new Dictionary<string, string> { ["region"] = "Unknown", ["component"] = "api" },
            server: "unprofiled-server", database: "unprofiled-db", engine: "postgres");
        seed.Operation(session, profile: "app", variables: new Dictionary<string, string> { ["region"] = "west", ["component"] = "api" });
        var last = seed.Operation(session, profile: null,
            variables: new Dictionary<string, string> { ["region"] = "Unknown", ["component"] = "portal" },
            server: "unprofiled-server", database: "unprofiled-db", engine: "postgres");

        var from = new DateTimeOffset(2026, 10, 7, 9, 0, 2, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 7, 9, 0, 5, TimeSpan.Zero);
        var dimensions = new Dictionary<string, string?> { ["region"] = "Unknown" };
        var stats = Reader(home).Stats(new StatsQuery(from, to, Dimensions: dimensions, RowDimension: "region", ColumnDimension: "component",
            UnprofiledOnly: true));
        var page = Reader(home).Operations(new OperationQuery(From: from, To: to, Dimensions: dimensions, UnprofiledOnly: true));

        Assert.Equal(1, stats.ProfileDimensions.Operations);
        Assert.False(stats.ProfileDimensions.ProfileDefinitionAvailable);
        Assert.Equal(("postgres", "unprofiled-server", "unprofiled-db", 1),
            (Assert.Single(stats.ProfileDimensions.Targets).Engine, stats.ProfileDimensions.Targets[0].Server,
                stats.ProfileDimensions.Targets[0].Database, stats.ProfileDimensions.Targets[0].Count));
        Assert.Equal(last.OperationId, Assert.Single(page.Items).Id);
        Assert.Equal(1, stats.ProfileDimensions.Matrix!.Totals.Operations);
    }

    [Fact]
    public void Token_coverage_counts_raw_only_emitted_only_missing_and_paired_rows()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:coverage");
        seed.Operation(session, tokens: SeedTokens.RawOnly);
        seed.Operation(session, tokens: SeedTokens.EmittedOnly);
        seed.Operation(session, tokens: SeedTokens.None);
        seed.Operation(session, tokens: SeedTokens.Both);

        var costs = Reader(home).Stats(new StatsQuery()).WorkflowCosts;

        var legacy = Assert.Single(costs);
        Assert.Equal((80L, 20L, 2), (legacy.OutputBytes, legacy.EstimatedOutputTokens, legacy.CallCount));
    }

    [Fact]
    public void Newer_schema_is_reported()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        Assert.Equal(99, Reader(home).SchemaVersion());
    }

    [Fact]
    public void Operation_carries_the_stored_error_message()
    {
        using var home = new TempHome();
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null, TimeProvider.System);
        var handle = journal.Begin(Journal.JournalTestData.Session(), Journal.JournalTestData.Start());
        journal.Complete(handle, Journal.JournalTestData.End("failed", 5) with { ErrorKind = "sql_execution_failed", ErrorMessage = "Invalid column name 'x'." });
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        var operation = reader.Operation(handle!.OperationId)!.Operation;

        Assert.Equal("sql_execution_failed", operation.ErrorKind);
        Assert.Equal("Invalid column name 'x'.", operation.ErrorMessage);
    }

    [Fact]
    public void Sessions_carry_annotations_and_the_detail_carries_the_note()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:a"));
        JournalSeed.Annotate(home.DatabasePath, "cli:a", title: "BSI-1 orders", note: "first line\r\nsecond", starred: true);
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        var summary = reader.Sessions(new SessionQuery(Starred: true)).Items.Single();
        Assert.Equal(("BSI-1 orders", true, false, "first line"), (summary.Title, summary.Starred, summary.Archived, summary.NotePreview));
        Assert.Equal("first line\r\nsecond", reader.Session(summary.Id)!.Note);
    }

    [Fact]
    public void Session_lists_split_by_star_and_archive()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        foreach (var key in new[] { "cli:plain", "cli:star", "cli:arch", "cli:star-arch" })
            seed.Operation(JournalSeed.Session(key));
        JournalSeed.Annotate(home.DatabasePath, "cli:star", starred: true);
        JournalSeed.Annotate(home.DatabasePath, "cli:arch", archived: true);
        JournalSeed.Annotate(home.DatabasePath, "cli:star-arch", starred: true, archived: true);
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        string[] Keys(SessionQuery query) => reader.Sessions(query).Items.Select(s => s.SessionKey).ToArray();
        Assert.Equal(["cli:plain"], Keys(new SessionQuery(Starred: false)));
        Assert.Equal(["cli:star"], Keys(new SessionQuery(Starred: true)));
        Assert.Equal(["cli:arch"], Keys(new SessionQuery(Archived: true, Starred: false)));
        Assert.Equal(["cli:star-arch"], Keys(new SessionQuery(Archived: true, Starred: true)));
        Assert.Equal(["cli:star", "cli:plain"], Keys(new SessionQuery()));
        Assert.Equal(["cli:star-arch", "cli:star", "cli:arch", "cli:plain"], Keys(new SessionQuery(Archived: null)));
    }

    [Fact]
    public void Search_matches_title_note_cwd_key_and_client_title_literally()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:title"));
        seed.Operation(JournalSeed.Session("cli:note"));
        seed.Operation(JournalSeed.Session("cli:wild"));
        JournalSeed.Annotate(home.DatabasePath, "cli:title", title: "BSI_123 orders");
        JournalSeed.Annotate(home.DatabasePath, "cli:note", note: "reads down 50% on Orders");
        JournalSeed.Annotate(home.DatabasePath, "cli:wild", title: "BSIX123");
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        string[] Keys(string q) => reader.Sessions(new SessionQuery(Search: q)).Items.Select(s => s.SessionKey).Order().ToArray();
        Assert.Equal(["cli:title"], Keys("bsi_123"));
        Assert.Equal(["cli:note"], Keys("50%"));
        Assert.Equal(["cli:note", "cli:title", "cli:wild"], Keys("cli:"));
        Assert.Equal(["cli:note", "cli:title", "cli:wild"], Keys("/work"));
    }

    [Fact]
    public void Search_treats_backslash_and_underscore_literally_and_matches_the_client_title()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:path") with { Cwd = @"C:\work\a_b" });
        seed.Operation(JournalSeed.Session("cli:underscore-decoy") with { Cwd = @"C:\work\aXb" });
        seed.Operation(JournalSeed.Session("cli:backslash-decoy") with { Cwd = "/worka_b" });
        seed.Operation(JournalSeed.Session("mcp:client") with { ClientTitle = "Claude_Code" });
        seed.Operation(JournalSeed.Session("mcp:client-decoy") with { ClientTitle = "ClaudeXCode" });
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        string[] Keys(string q) => reader.Sessions(new SessionQuery(Search: q)).Items.Select(s => s.SessionKey).Order().ToArray();
        Assert.Equal(["cli:path"], Keys(@"work\a_b"));
        Assert.Equal(["cli:path"], Keys(@"\a_"));
        Assert.Equal(["mcp:client"], Keys("claude_code"));
    }

    [Fact]
    public void Search_ignores_case_beyond_ascii_but_keeps_accents()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:city"));
        seed.Operation(JournalSeed.Session("cli:turtle") with { Cwd = "/praca/ŻÓŁW" });
        seed.Operation(JournalSeed.Session("cli:plain"));
        JournalSeed.Annotate(home.DatabasePath, "cli:city", title: "Raport ŁÓDŹ", note: "Zażółć gęślą jaźń");
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        string[] Keys(string q) => reader.Sessions(new SessionQuery(Search: q)).Items.Select(s => s.SessionKey).Order().ToArray();
        Assert.Equal(["cli:city"], Keys("łódź"));
        Assert.Equal(["cli:city"], Keys("ZAŻÓŁĆ GĘŚLĄ"));
        Assert.Equal(["cli:turtle"], Keys("żółw"));
        Assert.Equal(["cli:city"], Keys("łódź".Normalize(NormalizationForm.FormD)));
        Assert.Empty(Keys("lodz"));
        Assert.Equal(["cli:city", "cli:plain", "cli:turtle"], Keys("CLI:"));
    }

    [Fact]
    public void Search_folding_does_not_depend_on_the_process_culture()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:tr"));
        JournalSeed.Annotate(home.DatabasePath, "cli:tr", title: "INFO report");
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal(["cli:tr"], reader.Sessions(new SessionQuery(Search: "info")).Items.Select(s => s.SessionKey).ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Agent_title_comes_from_the_newest_linked_conversation_with_a_title()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:agent-title");
        var older = seed.Operation(session);
        var newer = seed.Operation(session);
        JournalSeed.Link(home.DatabasePath, older.OperationId, "claude", "older", "Older title");
        JournalSeed.Link(home.DatabasePath, newer.OperationId, "claude", "newer", "Newer title");
        var reader = Reader(home);
        var sessionId = Assert.Single(reader.Sessions(new SessionQuery()).Items).Id;

        Assert.Equal("Newer title", reader.Session(sessionId)!.Session.AgentTitle);

        JournalSeed.Link(home.DatabasePath, newer.OperationId, "claude", "newer", null);

        Assert.Equal("Older title", reader.Session(sessionId)!.Session.AgentTitle);
    }

    [Fact]
    public void Session_detail_lists_its_conversations_newest_first_with_operation_counts()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:agent-conversations");
        var first = seed.Operation(session);
        var second = seed.Operation(session);
        var third = seed.Operation(session);
        JournalSeed.Link(home.DatabasePath, first.OperationId, "claude", "conversation-a", "A");
        JournalSeed.Link(home.DatabasePath, second.OperationId, "claude", "conversation-a", "A");
        JournalSeed.Link(home.DatabasePath, third.OperationId, "codex", "conversation-b", "B");
        var sessionId = Assert.Single(Reader(home).Sessions(new SessionQuery()).Items).Id;

        Assert.Equal(
            [new AgentConversation("codex", "conversation-b", "B", 1), new AgentConversation("claude", "conversation-a", "A", 2)],
            Reader(home).Session(sessionId)!.Conversations);
    }

    [Fact]
    public void Effective_model_prefers_the_call_and_reports_its_source()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:effective-model");
        var call = seed.Operation(session, agent: new AgentCallMetadata("call-a", "gpt", "high", "s", "t", "exec", "user"));
        var transcriptOnly = seed.Operation(session);
        var neither = seed.Operation(session);
        JournalSeed.Link(home.DatabasePath, call.OperationId, "claude", "conversation-a", "A", "x", "low");
        JournalSeed.Link(home.DatabasePath, transcriptOnly.OperationId, "claude", "conversation-b", "B", "claude-opus-5-5", "high");
        var sessionId = Assert.Single(Reader(home).Sessions(new SessionQuery()).Items).Id;

        var operations = Reader(home).Session(sessionId)!.Operations.ToDictionary(operation => operation.Id);

        Assert.Equal(("gpt", "call"), (operations[call.OperationId].AgentModel, operations[call.OperationId].AgentModelSource));
        Assert.Equal(("claude-opus-5-5", "transcript"),
            (operations[transcriptOnly.OperationId].AgentModel, operations[transcriptOnly.OperationId].AgentModelSource));
        Assert.Equal((null, null), (operations[neither.OperationId].AgentModel, operations[neither.OperationId].AgentModelSource));
    }

    [Fact]
    public void Operation_detail_carries_transcript_model_and_conversation_title()
    {
        using var home = new TempHome();
        var operation = new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:transcript-detail"));
        JournalSeed.Link(home.DatabasePath, operation.OperationId, "claude", "conversation", "Transcript title", "claude-opus-5-5", "high");

        var agent = Reader(home).Operation(operation.OperationId)!.Agent;

        Assert.NotNull(agent);
        Assert.Equal(("claude-opus-5-5", "high", "Transcript title"),
            (agent.TranscriptModel, agent.TranscriptReasoningEffort, agent.ConversationTitle));
        Assert.Null(agent.Model);
        Assert.Null(agent.ReasoningEffort);
    }

    [Fact]
    public void Search_matches_the_agent_title()
    {
        using var home = new TempHome();
        var operation = new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:search-agent-title"));
        JournalSeed.Link(home.DatabasePath, operation.OperationId, "claude", "search-conversation", "Project Phoenix");

        var matches = Reader(home).Sessions(new SessionQuery(Search: "Phoenix")).Items;

        Assert.Equal("cli:search-agent-title", Assert.Single(matches).SessionKey);
    }

    [Fact]
    public void Note_preview_is_the_first_non_blank_line()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:a"));
        JournalSeed.Annotate(home.DatabasePath, "cli:a", note: "\n\n  real line\nmore");
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        Assert.Equal("real line", reader.Sessions(new SessionQuery()).Items.Single().NotePreview);
    }

    [Fact]
    public void Starred_list_pages_with_the_id_cursor_without_gaps()
    {
        using var home = new TempHome();
        // Two full pages of starred sessions plus a partial one is what proves the id cursor;
        // seeding ten times more only cost twelve seconds of the gate.
        JournalSeed.Operations(home.DatabasePath, 240, (index, id) =>
        {
            if (index % 2 == 0)
                JournalSeed.Annotate(home.DatabasePath, $"cli:{index:D3}", starred: true);
        });
        var reader = new JournalReader(home.DatabasePath, new FakeProcesses());

        var seen = new List<string>();
        long? cursor = null;
        do
        {
            var page = reader.Sessions(new SessionQuery(Starred: true, Cursor: cursor, Limit: 100));
            seen.AddRange(page.Items.Select(s => s.SessionKey));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(120, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }
}
