using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class OptimizationComparerTests
{
    [Fact]
    public void Measured_together_uses_that_operation()
    {
        var result = Compare([
            Evidence(201, "h1", "h2", (1000, 800)),
            Evidence(214, "h1", "h2", (2000, 500)),
        ], "v1", "v2");

        Assert.Equal(PairBasis.MeasuredTogether, result.Basis);
        Assert.Equal(214, result.OperationId);
        AssertAction(result.BeforeAction, 214, "baseline", "0");
        AssertAction(result.AfterAction, 214, "candidate", "0");
        Assert.Equal(2000, Assert.Single(result.Tables).ReadsBefore);
        Assert.Equal(2000, result.Elapsed.BeforeMs);
    }

    [Fact]
    public void Different_runs_keep_the_actual_source_operation_and_variant_for_each_version()
    {
        var compares = new[]
        {
            Evidence(201, "h0", "h1", (1200, 1000)),
            Evidence(214, "h2", "h3", (900, 700)),
        };

        var result = OptimizationComparer.Compare(Group("h0", "h1", "h2", "h3"), compares, "v1", "v2", "all", "sum");

        Assert.Equal(PairBasis.DifferentRunsSameConditions, result.Basis);
        Assert.Null(result.OperationId);
        AssertAction(result.BeforeAction, 201, "candidate", "0");
        AssertAction(result.AfterAction, 214, "baseline", "0");
    }

    [Fact]
    public void Different_runs_same_conditions_marks_different_run()
    {
        var compares = new[]
        {
            Evidence(201, "h0", "h1", (1200, 1000)),
            Evidence(214, "h2", "h3", (900, 700)),
        };
        var group = Group("h0", "h1", "h2", "h3");

        var result = OptimizationComparer.Compare(group, compares, "v1", "v3", "all", "sum");

        Assert.Equal(PairBasis.DifferentRunsSameConditions, result.Basis);
        Assert.True(result.Elapsed.DifferentRun);
    }

    [Fact]
    public void Common_compare_beats_newer_measure_observations()
    {
        var compare = Evidence(214, "h0", "h1", (1000, 800));
        var evidence = new[] { compare, MeasureEvidence(215, "h0", 500), MeasureEvidence(216, "h1", 300) };
        var group = Assert.Single(Assert.Single(OptimizationSummaryBuilder.Build(evidence).Subjects).Groups);

        var result = OptimizationComparer.Compare(group, evidence, "v0", "v1", "all", "sum");

        Assert.Equal(PairBasis.MeasuredTogether, result.Basis);
        Assert.Equal(214, result.OperationId);
        Assert.Equal(1000, Assert.Single(result.Tables).ReadsBefore);
        Assert.Equal(800, Assert.Single(result.Tables).ReadsAfter);
    }

    [Fact]
    public void Cases_pair_by_measurement_fingerprint_when_case_ids_differ()
    {
        var compare = Evidence(214, "h0", "h1", (1000, 800)) with
        {
            Cases = [new CompareCaseEvidence("matrix-0", "Tenant A", null, true, null) { MeasurementFingerprint = "case-fp-a" }],
            Metrics = [new CompareMetricEvidence("baseline", "matrix-0", "Tenant A", 1000, 1000, 1, []) { MeasurementFingerprint = "case-fp-a" },
                new CompareMetricEvidence("candidate", "matrix-0", "Tenant A", 800, 800, 1, []) { MeasurementFingerprint = "case-fp-a" }],
        };
        var measure = MeasureEvidence(215, "h2", 700) with
        {
            Cases = [new CompareCaseEvidence("set-tenant-a", "Tenant A", "Tenant A", null, null) { MeasurementFingerprint = "case-fp-a" }],
            Metrics = [new CompareMetricEvidence("measure", "set-tenant-a", "Tenant A", 700, 700, 1, []) { MeasurementFingerprint = "case-fp-a" }],
        };
        var evidence = new[] { compare, measure };
        var group = Assert.Single(Assert.Single(OptimizationSummaryBuilder.Build(evidence).Subjects).Groups);

        var result = OptimizationComparer.Compare(group, evidence, "v0", "v2", "all", "sum");

        Assert.Equal(PairBasis.DifferentRunsSameConditions, result.Basis);
        Assert.Equal("case-fp-a", Assert.Single(result.Cases).Id);
        Assert.Equal("Tenant A", Assert.Single(result.Cases).Label);
        AssertAction(result.BeforeAction, 214, "baseline", "matrix-0");
        AssertAction(result.AfterAction, 215, "measure", "set-tenant-a");
        Assert.Equal("matrix-0", result.BeforeAction?.CaseIdsByFingerprint["case-fp-a"]);
        Assert.Equal("set-tenant-a", result.AfterAction?.CaseIdsByFingerprint["case-fp-a"]);
    }

    [Fact]
    public void Measure_pairs_use_logical_read_outcomes_and_mixed_case_warning()
    {
        var before = MeasureEvidence(215, "h0", 0) with
        {
            Cases = [new CompareCaseEvidence("a", "Tenant A", null, null, null) { MeasurementFingerprint = "case-a" },
                new CompareCaseEvidence("b", "Tenant B", null, null, null) { MeasurementFingerprint = "case-b" }],
            Metrics = [new CompareMetricEvidence("measure", "a", "Tenant A", 1000, 10, 1, []) { MeasurementFingerprint = "case-a" },
                new CompareMetricEvidence("measure", "b", "Tenant B", 1000, 10, 1, []) { MeasurementFingerprint = "case-b" }],
        };
        var after = MeasureEvidence(216, "h1", 0) with
        {
            Cases = [new CompareCaseEvidence("set-a", "Tenant A", "A", null, null) { MeasurementFingerprint = "case-a" },
                new CompareCaseEvidence("set-b", "Tenant B", "B", null, null) { MeasurementFingerprint = "case-b" }],
            Metrics = [new CompareMetricEvidence("measure", "set-a", "Tenant A", 800, 8, 1, []) { MeasurementFingerprint = "case-a" },
                new CompareMetricEvidence("measure", "set-b", "Tenant B", 1200, 12, 1, []) { MeasurementFingerprint = "case-b" }],
        };
        var evidence = new[] { before, after };
        var group = Assert.Single(Assert.Single(OptimizationSummaryBuilder.Build(evidence).Subjects).Groups);

        var result = OptimizationComparer.Compare(group, evidence, "v0", "v1", "all", "sum");

        Assert.Equal("better", result.Cases[0].Outcome);
        Assert.Equal("worse", result.Cases[1].Outcome);
        Assert.Equal((1, 1, "Tenant B"), (result.Mixed?.Better, result.Mixed?.Worse, result.Mixed?.WorstCaseLabel));
        Assert.Equal("results not compared", result.EquivalenceLabel);
    }

    [Fact]
    public void Measure_pairs_with_different_conditions_are_not_compared()
    {
        var before = MeasureEvidence(215, "h0", 100);
        var after = MeasureEvidence(216, "h1", 50) with { Fingerprint = "other-fingerprint" };
        var evidence = new[] { before, after };
        var group = Group("h0", "h1") with { Subject = "orders", Database = "db" };

        var result = OptimizationComparer.Compare(group, evidence, "v0", "v1", "all", "sum");

        Assert.Equal(PairBasis.ConditionsDiffer, result.Basis);
        Assert.Equal("conditions differ", result.EquivalenceLabel);
        Assert.Empty(result.Tables);
        Assert.Null(result.Elapsed.BeforeMs);
    }

    [Fact]
    public void Conditions_differ_returns_no_numbers()
    {
        var group = Group("h1");

        var result = OptimizationComparer.Compare(group, [Evidence(214, "h1", "h2", (100, 50))], "v1", "missing", "all", "sum");

        Assert.Equal(PairBasis.ConditionsDiffer, result.Basis);
        Assert.Empty(result.Tables);
        Assert.Null(result.Elapsed.BeforeMs);
        Assert.Null(result.Elapsed.AfterMs);
    }

    [Fact]
    public void Duplicate_version_labels_across_scopes_are_not_joined()
    {
        var group = Group("h1", "h2") with
        {
            Scopes =
            [
                new ScopeVersions("whole query", [Version("v1", "h1"), Version("v2", "h2")]),
                new ScopeVersions("fragment", [Version("v1", "h3"), Version("v2", "h4")]),
            ],
        };

        var result = OptimizationComparer.Compare(group, [Evidence(214, "h1", "h2", (100, 50))], "v1", "v2", "all", "sum");

        Assert.Equal(PairBasis.ConditionsDiffer, result.Basis);
        Assert.Empty(result.Tables);
    }

    [Fact]
    public void Builder_identity_filters_same_fingerprint_and_scope_collisions()
    {
        var target = Evidence(201, "h1", "h2", (1000, 900));
        var otherSubject = Evidence(214, "h1", "h2", (1000, 100)) with { Subject = "customers" };
        var otherDatabase = Evidence(216, "h1", "h2", (1000, 50)) with { Database = "db2" };
        var evidence = new[] { target, otherSubject, otherDatabase };
        var group = Assert.Single(Assert.Single(OptimizationSummaryBuilder.Build([target]).Subjects).Groups);

        var result = OptimizationComparer.Compare(group, evidence, "v0", "v1", "all", "sum");

        Assert.Equal(201, result.OperationId);
        Assert.Equal(900, Assert.Single(result.Tables).ReadsAfter);
    }

    [Fact]
    public void Unidentified_group_rejects_colliding_evidence()
    {
        var group = Group("h1", "h2");
        var evidence = new[]
        {
            Evidence(201, "h1", "h2", (100, 90)),
            Evidence(214, "h1", "h2", (100, 20)) with { Subject = "customers" },
        };

        var result = OptimizationComparer.Compare(group, evidence, "v1", "v2", "all", "sum");

        Assert.Equal(PairBasis.ConditionsDiffer, result.Basis);
        Assert.Empty(result.Tables);
        Assert.Null(result.Elapsed.AfterMs);
    }

    [Fact]
    public void Identical_versions_show_same_sql()
    {
        var result = Compare([Evidence(214, "h1", "h1", (1000, 1000))], "v1", "v1");

        Assert.Equal(PairBasis.SameSql, result.Basis);
        Assert.Equal("same SQL", result.EquivalenceLabel);
    }

    [Theory]
    [InlineData(1000, 985, "better")]
    [InlineData(1000, 995, "same")]
    [InlineData(500, 505, "same")]
    public void Case_outcome_uses_both_read_thresholds(long before, long after, string expected)
    {
        var result = Compare([Evidence(214, "h1", "h2", (before, after))], "v1", "v2");

        Assert.Equal(expected, Assert.Single(result.Cases).Outcome);
    }

    [Fact]
    public void Results_differ_overrides_read_outcome()
    {
        var evidence = Evidence(214, "h1", "h2", (1000, 100)) with
        {
            ResultsEquivalent = false,
            Cases = [new CompareCaseEvidence("0", "@CustomerId = 9001", null, false, null)],
        };

        var result = Compare([evidence], "v1", "v2");

        Assert.Equal("results differ", Assert.Single(result.Cases).Outcome);
        Assert.Equal("results differ", result.EquivalenceLabel);
    }

    [Fact]
    public void Mixed_names_worst_case()
    {
        var result = Compare([Evidence(214, "h1", "h2", (1000, 1350), (1000, 50), (1000, 400))], "v1", "v2");

        Assert.Equal(new MixedWarning(2, 1, "@CustomerId = 9001", 35), result.Mixed);
    }

    [Fact]
    public void Sum_and_mean_aggregate_per_case_medians()
    {
        var compares = new[] { Evidence(214, "h1", "h2", (1000, 500), (1000, 700)) };
        var group = Group("h1", "h2");

        var sum = OptimizationComparer.Compare(group, compares, "v1", "v2", "all", "sum");
        var mean = OptimizationComparer.Compare(group, compares, "v1", "v2", "all", "mean");

        Assert.Equal(2000, sum.Tables.Single().ReadsBefore);
        Assert.Equal(1200, sum.Tables.Single().ReadsAfter);
        Assert.Equal(1000, mean.Tables.Single().ReadsBefore);
        Assert.Equal(600, mean.Tables.Single().ReadsAfter);
        Assert.Contains("sum", sum.Aggregate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mean", mean.Aggregate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Partial_cases_aggregate_over_common_cases()
    {
        var evidence = Evidence(214, "h1", "h2", (1000, 500), (1000, 700), (1000, 300)) with
        {
            Metrics =
            [
                Metric("baseline", "0", 1000), Metric("candidate", "0", 500),
                Metric("baseline", "1", 1000), Metric("candidate", "1", 700),
                Metric("baseline", "2", 1000),
            ],
        };

        var result = Compare([evidence], "v1", "v2");

        Assert.Equal("2 of 3 cases", result.CoverageLabel);
        Assert.Equal(2000, result.Tables.Single().ReadsBefore);
        Assert.Equal(1200, result.Tables.Single().ReadsAfter);
    }

    [Fact]
    public void Zero_baseline_reads_show_absolute_change()
    {
        var result = Compare([Evidence(214, "h1", "h2", (0, 5))], "v1", "v2");

        Assert.Equal("+5 pages", Assert.Single(result.Tables).Change);
    }

    [Fact]
    public void Missing_read_metrics_do_not_create_a_same_outcome()
    {
        var evidence = Evidence(214, "h1", "h2", (100, 100)) with
        {
            Metrics =
            [
                Metric("baseline", "0", 100) with { LogicalReadsMedian = null },
                Metric("candidate", "0", 100) with { LogicalReadsMedian = null },
            ],
        };

        var result = Compare([evidence], "v1", "v2");

        Assert.Empty(result.Cases);
        Assert.Empty(result.Tables);
        Assert.Null(result.Elapsed.BeforeMs);
        Assert.Null(result.Elapsed.AfterMs);
    }

    [Fact]
    public void Missing_lob_table_reads_remain_unknown()
    {
        var evidence = Evidence(214, "h1", "h2", (100, 90)) with
        {
            Metrics =
            [
                Metric("baseline", "0", 100) with { TableRows = [new CompareTableIoEvidence("Orders", 90, null, 1)] },
                Metric("candidate", "0", 90) with { TableRows = [new CompareTableIoEvidence("Orders", 80, null, 1)] },
            ],
        };

        var result = Compare([evidence], "v1", "v2");

        Assert.Null(Assert.Single(result.Tables).ReadsBefore);
        Assert.Equal("—", Assert.Single(result.Tables).Change);
    }

    [Fact]
    public void Temp_objects_and_zero_read_rows_collapse_and_real_tables_sort_by_change()
    {
        var evidence = Evidence(214, "h1", "h2", (100, 100)) with
        {
            Metrics =
            [
                TableMetric("baseline", ("Small", 0), ("@T", 0), ("Worktable", 0), ("Workfile", 0), ("Orders", 100), ("Customers", 100)),
                TableMetric("candidate", ("Small", 0), ("@T", 0), ("Worktable", 0), ("Workfile", 0), ("Orders", 20), ("Customers", 70)),
            ],
        };

        var result = Compare([evidence], "v1", "v2");

        Assert.Equal(["Orders", "Customers"], result.Tables.Select(row => row.Table));
        Assert.Equal(4, result.Collapsed?.Count);
        Assert.Equal("table variables / worktables (4, 0 reads)", result.Collapsed?.Label);
    }

    [Fact]
    public void Equivalence_labels_distinguish_direct_chain_and_uncompared()
    {
        var direct = Compare([Evidence(214, "h1", "h2", (100, 90))], "v1", "v2");
        var via = Compare([
            Evidence(201, "h1", "h2", (100, 90)),
            Evidence(214, "h2", "h3", (90, 80)),
        ], "v1", "v3");
        var viaGroup = Group("h1", "h2", "h3") with
        {
            Scopes =
            [
                new ScopeVersions("fragment", [Version("v8", "h2")]),
                new ScopeVersions("whole query", [Version("v1", "h1"), Version("v2", "h2"), Version("v3", "h3")]),
            ],
        };
        var viaSelectedScope = OptimizationComparer.Compare(viaGroup,
            [Evidence(201, "h1", "h2", (100, 90)), Evidence(214, "h2", "h3", (90, 80))],
            "v1", "v3", "all", "sum");
        var viaReverse = Compare([
            Evidence(201, "h2", "h1", (90, 100)),
            Evidence(214, "h3", "h2", (80, 90)),
        ], "v1", "v3");
        var unrelated = OptimizationComparer.Compare(Group("h1", "h2", "h3", "h4"),
            [Evidence(201, "h1", "h2", (100, 90)), Evidence(214, "h3", "h4", (80, 70))],
            "v1", "v3", "all", "sum");

        Assert.Equal("results equivalent (direct)", direct.EquivalenceLabel);
        Assert.Equal("results equivalent via v2", via.EquivalenceLabel);
        Assert.Equal("results equivalent via v2", viaSelectedScope.EquivalenceLabel);
        Assert.Equal("results equivalent via v2", viaReverse.EquivalenceLabel);
        Assert.Equal("results not compared", unrelated.EquivalenceLabel);
    }

    [Fact]
    public void Engine_without_table_io_returns_message_and_postgres_metrics()
    {
        var evidence = Evidence(214, "h1", "h2", (100, 80)) with
        {
            Engine = "postgres",
            Metrics =
            [
                Metric("baseline", "0", 100) with { Postgres = new PostgresBuffers(7, 2, 1, 0, 0, 3), CpuMedianMs = 12 },
                Metric("candidate", "0", 80) with { Postgres = new PostgresBuffers(5, 1, 0, 0, 0, 1), CpuMedianMs = 8 },
            ],
        };

        var result = Compare([evidence], "v1", "v2");

        Assert.True(result.TableIoUnavailable);
        Assert.Empty(result.Tables);
        Assert.Equal(12, result.CpuBeforeMs);
        Assert.Equal(8, result.CpuAfterMs);
        Assert.Equal(new PostgresBuffers(7, 2, 1, 0, 0, 3), result.BuffersBefore);
        Assert.Equal(new PostgresBuffers(5, 1, 0, 0, 0, 1), result.BuffersAfter);
    }

    [Fact]
    public void Postgres_cases_without_logical_reads_keep_available_elapsed_cpu_and_buffers()
    {
        var evidence = Evidence(214, "h1", "h2", (100, 80)) with
        {
            Engine = "postgres",
            Metrics =
            [
                Metric("baseline", "0", 100) with { LogicalReadsMedian = null, ElapsedMedianMs = 12, CpuMedianMs = 10, Postgres = new PostgresBuffers(7, 2, 1, 0, 0, 3) },
                Metric("candidate", "0", 80) with { LogicalReadsMedian = null, ElapsedMedianMs = 8, CpuMedianMs = 6, Postgres = new PostgresBuffers(5, 1, 0, 0, 0, 1) },
            ],
        };

        var result = Compare([evidence], "v1", "v2");

        Assert.Single(result.Cases);
        Assert.Equal("unavailable", Assert.Single(result.Cases).Outcome);
        Assert.True(result.TableIoUnavailable);
        Assert.Empty(result.Tables);
        Assert.Equal(12, result.Elapsed.BeforeMs);
        Assert.Equal(8, result.Elapsed.AfterMs);
        Assert.Equal(10, result.CpuBeforeMs);
        Assert.Equal(6, result.CpuAfterMs);
        Assert.Equal(new PostgresBuffers(7, 2, 1, 0, 0, 3), result.BuffersBefore);
        Assert.Equal(new PostgresBuffers(5, 1, 0, 0, 0, 1), result.BuffersAfter);
    }

    private static OptimizationComparison Compare(IReadOnlyList<CompareEvidence> evidence, string a, string b) =>
        OptimizationComparer.Compare(Group("h1", "h2", "h3"), evidence, a, b, "all", "sum");

    private static void AssertAction(OptimizationActionReference? action, long operationId, string side, string caseId)
    {
        Assert.NotNull(action);
        Assert.Equal(operationId, action.OperationId);
        Assert.Equal(side, action.Side);
        Assert.Equal([caseId], action.CaseIds);
    }

    private static ConditionsGroup Group(params string[] hashes)
    {
        var versions = hashes.Select((hash, index) => new VersionRow(VersionName(hash, index), hash, null, null, null, null,
            "incomplete", [], 0)).ToArray();
        return new ConditionsGroup("fingerprint", "1 case: @CustomerId = 9001",
            [new ScopeVersions("whole query", versions)], "v0", versions.Length > 1 ? versions[^1].Version : null);
    }

    private static string VersionName(string hash, int index) => hash.StartsWith("h", StringComparison.Ordinal)
        && int.TryParse(hash.AsSpan(1), out var number) ? $"v{number}" : $"v{index}";

    private static VersionRow Version(string version, string hash) =>
        new(version, hash, null, null, null, null, "incomplete", [], 0);

    private static CompareEvidence Evidence(long id, string baselineHash, string candidateHash,
        params (long Before, long After)[] reads)
    {
        var metrics = new List<CompareMetricEvidence>();
        for (var index = 0; index < reads.Length; index++)
        {
            metrics.Add(Metric("baseline", index.ToString(), reads[index].Before));
            metrics.Add(Metric("candidate", index.ToString(), reads[index].After));
        }
        var cases = reads.Select((_, index) => new CompareCaseEvidence(index.ToString(), $"@CustomerId = {9001 + index}", null, true, null)).ToArray();
        return new CompareEvidence(id, "orders", "whole", "db", "sqlserver", "fingerprint", baselineHash, candidateHash,
            $"2026-10-07T09:00:{id % 60:D2}Z", "succeeded", "Ordered", true, null, cases, metrics);
    }

    private static CompareMetricEvidence Metric(string variant, string caseId, long reads) =>
        Metric(variant, caseId, reads, "Orders");

    private static CompareEvidence MeasureEvidence(long id, string hash, long reads) =>
        new(id, "orders", "whole", "db", "sqlserver", "fingerprint", hash, null,
            $"2026-10-07T09:00:{id % 60:D2}Z", "succeeded", null, null, null,
            [new CompareCaseEvidence("0", "@CustomerId = 9001", null, null, null)],
            [Metric("measure", "0", reads)])
        { IsMeasure = true };

    private static CompareMetricEvidence Metric(string variant, string caseId, long reads, string table) =>
        new(variant, caseId, $"@CustomerId = {9001 + int.Parse(caseId, System.Globalization.CultureInfo.InvariantCulture)}",
            reads, reads, 1, [new CompareTableIoEvidence(table, reads, 0, 1)]);

    private static CompareMetricEvidence TableMetric(string variant, params (string Table, long Reads)[] tables) =>
        new(variant, "0", "@CustomerId = 9001", 100, 100, 1,
            tables.Select(table => new CompareTableIoEvidence(table.Table, table.Reads, 0, 1)).ToArray());
}