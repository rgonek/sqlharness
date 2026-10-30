using System.Text.Json;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class OptimizationComparerTests
{
    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("running")]
    [InlineData("abandoned")]
    public void Ineligible_attempt_alone_supplies_no_pair_evidence(string status)
    {
        var attempt = Evidence(1, "h1", "h2", (100, 50)) with { Status = status };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [attempt], "v1", "v2", "all", "sum");
        Assert.Null(result.OperationId);
        Assert.Empty(result.Cases);
        Assert.Null(result.WholeBatch.Before);
        Assert.Null(result.WholeBatch.After);
    }

    [Fact]
    public void Successful_policy_failure_still_supplies_direct_pair()
    {
        var attempt = Evidence(1, "h1", "h2", (100, 50)) with { RegressionVerdict = "fail" };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [attempt], "v1", "v2", "all", "sum");
        Assert.Equal(PairBasis.MeasuredTogether, result.Basis);
        Assert.Equal(1, result.OperationId);
        Assert.Equal(100, result.WholeBatch.Before);
        Assert.Equal(50, result.WholeBatch.After);
    }

    [Fact]
    public void Metadata_only_attempt_does_not_replace_recorded_direct_pair()
    {
        var good = Evidence(1, "h1", "h2", (100, 50));
        var statement = new CompareStatementEvidence("key", 0, "SELECT 1", "plan", "confirmed", null, null, null, [], []);
        var metadata = new[] { "baseline", "candidate" }.Select(variant =>
            new CompareMetricEvidence(variant, "0", "case", null, null, null, []) { Statements = [statement] }).ToArray();
        var later = Evidence(2, "h1", "h2", (1, 1)) with { Metrics = metadata };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [good, later], "v1", "v2", "all", "sum");
        Assert.Equal(1, result.OperationId);
        Assert.Equal(100, result.WholeBatch.Before);
        Assert.Equal(50, result.WholeBatch.After);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("running")]
    [InlineData("abandoned")]
    public void Ineligible_attempt_does_not_override_successful_pair(string status)
    {
        var good = Evidence(1, "h1", "h2", (100, 50));
        var interrupted = Evidence(2, "h1", "h2", (999, 888)) with { Status = status };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [good, interrupted], "v1", "v2", "all", "sum");
        Assert.Equal(1, result.OperationId);
        Assert.Equal(100, result.WholeBatch!.Before);
        Assert.Equal(50, result.WholeBatch.After);
    }

    [Theory]
    [InlineData(PairBasis.MeasuredTogether, "MeasuredTogether")]
    [InlineData(PairBasis.DifferentRunsSameConditions, "DifferentRunsSameConditions")]
    [InlineData(PairBasis.ConditionsDiffer, "ConditionsDiffer")]
    [InlineData(PairBasis.SameSql, "SameSql")]
    public void Pair_basis_json_matches_dashboard_frontend_contract(PairBasis basis, string expected)
    {
        var json = JsonSerializer.SerializeToElement(new { basis }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expected, json.GetProperty("basis").GetString());
    }

    [Fact]
    public void Statement_projections_preserve_side_availability_and_are_unavailable_for_all_cases()
    {
        var before = Statement("work", "SELECT work", 45, "old") with
        {
            CpuMs = 0,
            ElapsedMs = 17,
            PlanRepetition = 2,
            GrantRequestedKb = 30,
            GrantGrantedKb = 20,
            GrantMaxUsedKb = 10,
            HasSpillWarning = false,
            Objects = [new TableRow("Orders", 1, 1, 45, 45, "0%") { ObjectKind = "table", LobLogicalReadsBefore = 5, LobLogicalReadsAfter = 5 }],
        };
        var after = Statement("work", "SELECT work", 20, "new") with
        {
            Objects = [new TableRow("Orders", 1, 1, 20, 20, "0%") { ObjectKind = "table" }],
        };
        var evidence = Evidence(203, "h1", "h2", (45, 20)) with
        {
            Metrics = [StatementMetric("baseline", 0, 45, before), StatementMetric("candidate", 0, 20, after)],
        };
        OptimizationComparison Run(string selected) => OptimizationComparer.Compare(Group("h1", "h2"), [evidence], "v1", "v2", selected, "sum", "statements");
        var row = Assert.Single(Run("0").Statements);
        Assert.Equal(0, row.CpuBeforeMs);
        Assert.Equal(17, row.ElapsedBeforeMs);
        Assert.Equal(2, row.PlanRepetitionBefore);
        Assert.Equal(30, row.GrantRequestedKbBefore);
        Assert.Equal(20, row.GrantGrantedKbBefore);
        Assert.Equal(10, row.GrantMaxUsedKbBefore);
        Assert.False(row.HasSpillWarningBefore);
        Assert.Null(row.CpuAfterMs);
        Assert.Equal(5, Assert.Single(row.Objects).LobLogicalReadsBefore);
        Assert.Null(Assert.Single(row.Objects).LobLogicalReadsAfter);
        Assert.Equal(45, row.ReadsBefore);
        var allResult = Run("all");
        Assert.NotNull(allResult.StatementMetricsUnavailableReason);
        var all = Assert.Single(allResult.Statements);
        Assert.Null(all.CpuBeforeMs);
        Assert.Null(all.GrantRequestedKbBefore);
        Assert.Null(all.HasSpillWarningBefore);
        Assert.Null(all.PlanRepetitionBefore);
        Assert.Null(Assert.Single(all.Objects).LobLogicalReadsBefore);
        evidence = evidence with { Metrics = [evidence.Metrics[0] with { StatementAttribution = "partial" }, evidence.Metrics[1]] };
        Assert.Equal(0, Assert.Single(Run("0").Statements).CpuBeforeMs);
        Assert.Equal(30, Assert.Single(Run("0").Statements).GrantRequestedKbBefore);
    }

    [Fact]
    public void Ambiguous_or_incomplete_object_contributions_have_no_lob_value()
    {
        var before = Statement("work", "SELECT work", 45, "old") with
        {
            Objects = [
                new TableRow("Orders", 1, 1, 20, 20, "0%") { ObjectKind = "table", LobLogicalReadsBefore = 5 },
                new TableRow("Orders", 1, 1, 25, 25, "0%") { ObjectKind = "index", LobLogicalReadsBefore = 7 },
            ],
        };
        var after = Statement("work", "SELECT work", 20, "new") with
        {
            Objects = [new TableRow("Orders", 1, 1, 20, 20, "0%") { ObjectKind = "table", LobLogicalReadsAfter = 3 }],
        };
        var evidence = Evidence(203, "h1", "h2", (45, 20)) with
        {
            Metrics = [StatementMetric("baseline", 0, 45, before), StatementMetric("candidate", 0, 20, after)],
        };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [evidence], "v1", "v2", "0", "sum", "statements");
        Assert.Null(Assert.Single(Assert.Single(result.Statements).Objects).LobLogicalReadsBefore);
        before = before with { Objects = [before.Objects[0], before.Objects[1] with { LobLogicalReadsBefore = null }] };
        evidence = evidence with { Metrics = [StatementMetric("baseline", 0, 45, before), evidence.Metrics[1]] };
        result = OptimizationComparer.Compare(Group("h1", "h2"), [evidence], "v1", "v2", "0", "sum", "statements");
        Assert.Null(Assert.Single(Assert.Single(result.Statements).Objects).LobLogicalReadsBefore);
        before = before with { Objects = [before.Objects[1] with { LobLogicalReadsBefore = 7 }] };
        evidence = evidence with { Metrics = [StatementMetric("baseline", 0, 45, before), evidence.Metrics[1]] };
        result = OptimizationComparer.Compare(Group("h1", "h2"), [evidence], "v1", "v2", "0", "sum", "statements");
        var differentKinds = Assert.Single(Assert.Single(result.Statements).Objects);
        Assert.Null(differentKinds.LobLogicalReadsBefore);
        Assert.Null(differentKinds.LobLogicalReadsAfter);
    }

    [Fact]
    public void Occurrences_and_unmatched_hidden_work_do_not_mix_new_evidence()
    {
        var first = Statement("work", "SELECT same", 10, "a") with { CpuMs = 11, PlanRepetition = 1 };
        var second = Statement("work", "SELECT same", 20, "b") with { Occurrence = 1, CpuMs = 22, PlanRepetition = 2 };
        var unmatched = Statement("other", "SELECT other", 5, "c") with
        {
            CpuMs = 33,
            HiddenWork = [new SqlHarness.Core.HiddenWork("udf", "fn", 1, null)],
        };
        var evidence = Evidence(203, "h1", "h2", (35, 10)) with
        {
            Metrics = [StatementMetric("baseline", 0, 35, first, second, unmatched),
                StatementMetric("candidate", 0, 10, first with { CpuMs = 44 })],
        };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [evidence], "v1", "v2", "0", "sum", "statements");
        var occurrence0 = Assert.Single(result.Statements, row => row.Key == "work" && row.Occurrence == 0);
        var occurrence1 = Assert.Single(result.Statements, row => row.Key == "work" && row.Occurrence == 1);
        Assert.Equal(11, occurrence0.CpuBeforeMs);
        Assert.Equal(44, occurrence0.CpuAfterMs);
        Assert.Equal(22, occurrence1.CpuBeforeMs);
        Assert.Equal(2, occurrence1.PlanRepetitionBefore);
        Assert.Null(occurrence1.CpuAfterMs);
        var hidden = Assert.Single(result.Statements, row => row.Key == "other");
        Assert.Equal(33, hidden.CpuBeforeMs);
        Assert.Null(hidden.CpuAfterMs);
        Assert.Single(hidden.HiddenWork);
    }

    [Fact]
    public void Op203_shape_focuses_on_changed_statement()
    {
        var evidence = Evidence(203, "h1", "h2", (12010, 11960)) with
        {
            Metrics = [
                StatementMetric("baseline", 0, 12010,
                    Statement("prep", "SELECT prep", 11910, "prep-plan"), Statement("work", "SELECT work", 100, "old-plan")),
                StatementMetric("candidate", 0, 11960,
                    Statement("prep", "SELECT prep", 11910, "prep-plan"), Statement("work", "SELECT work", 50, "new-plan")),
            ],
        };

        var result = Compare([evidence], "v1", "v2");

        Assert.Equal("better", Assert.Single(result.Cases).Outcome);
        Assert.Equal("changed statements", result.VerdictBasis);
        Assert.Equal("whole batch: 12010 → 11960 reads (shared statements: 11910)", result.WholeBatch.Label);
        Assert.Equal("changed", result.View);
        Assert.Equal("work", Assert.Single(result.Statements, row => row.Status != "shared").Key);
    }

    [Fact]
    public void Partial_attribution_falls_back_to_whole_batch_without_collapsing()
    {
        var compare = Evidence(203, "h1", "h2", (12010, 11960)) with
        {
            Metrics = [
                StatementMetric("baseline", 0, 12010, Statement("prep", "SELECT prep", 11910, "p") with { Status = "partial" }, Statement("work", "SELECT work", 100, "old")) with { StatementAttribution = "partial" },
                StatementMetric("candidate", 0, 11960, Statement("prep", "SELECT prep", 11910, "p"), Statement("work", "SELECT work", 50, "new")),
            ],
        };

        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "all", "sum", "whole");

        Assert.Equal("whole batch", result.VerdictBasis);
        Assert.Equal("per-statement attribution incomplete", result.FallbackNote);
        Assert.Equal("whole", result.View);
        Assert.Contains(result.Statements, row => row.Key == "prep");
        var work = Assert.Single(result.Statements, row => row.Key == "work");
        Assert.Equal(100, work.ReadsBefore);
        Assert.Equal(50, work.ReadsAfter);
        Assert.Null(Assert.Single(result.Statements, row => row.Key == "prep").ReadsBefore);
        Assert.Equal("same", Assert.Single(result.Cases).Outcome);

        var defaultView = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "all", "sum", "changed");
        Assert.Equal("whole", defaultView.View);
        Assert.Equal("whole batch", defaultView.VerdictBasis);
    }

    [Fact]
    public void Partial_coverage_and_ambiguous_object_identity_do_not_invent_values()
    {
        var a = Statement("work", "SELECT work", 20, "p") with
        { Objects = [new TableRow("Thing", 1, 1, 20, 20, "") { ObjectKind = "table" }] };
        var b = a with { Objects = [new TableRow("Thing", 1, 1, 10, 10, "") { ObjectKind = "index" }] };
        var compare = Evidence(203, "h1", "h2", (20, 10)) with
        { Metrics = [StatementMetric("baseline", 0, 20, a), StatementMetric("candidate", 0, 10, b)] };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "0", "sum", "statements");
        var obj = Assert.Single(Assert.Single(result.Statements).Objects);
        Assert.Null(obj.ReadsBefore);
        Assert.Null(obj.ReadsAfter);
        Assert.Null(obj.ScansBefore);
        Assert.Equal("ambiguous attribution", obj.Availability!["reads"].Before.Reason);
    }

    [Fact]
    public void All_cases_report_partial_metric_coverage_without_aggregating_missing_evidence()
    {
        var compare = Evidence(203, "h1", "h2", (100, 90), (120, 110)) with
        {
            Metrics = [CaseMetric("baseline", "0", 100, Statement("work", "SELECT work", 10, "p")),
                CaseMetric("candidate", "0", 90, Statement("work", "SELECT work", 9, "p")),
                CaseMetric("baseline", "1", 120, Statement("work", "SELECT work", 12, "p") with { Status = "partial" }) with { StatementAttribution = "partial" },
                CaseMetric("candidate", "1", 110, Statement("work", "SELECT work", 11, "p"))],
        };
        var row = Assert.Single(OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "all", "sum", "statements").Statements);
        Assert.Null(row.ReadsBefore);
        Assert.Equal(20, row.ReadsAfter);
        Assert.Equal(1, row.Availability!["reads"].Before.AvailableCases);
        Assert.Equal(2, row.Availability["reads"].Before.TotalCases);
        Assert.Equal("ambiguous attribution", row.Availability["reads"].Before.Reason);
        Assert.Equal("single case required", row.Availability["cpu"].Before.Reason);
    }

    [Fact]
    public void Omitted_statement_and_missing_object_evidence_are_not_zero()
    {
        var measured = Statement("work", "SELECT work", 20, "p") with
        { Objects = [new TableRow("Thing", 1, 1, 20, 20, "") { ObjectKind = "table" }] };
        var noObjects = measured with { Reads = null, Scans = null, Objects = [] };
        var compare = Evidence(203, "h1", "h2", (20, 10), (0, 0)) with
        {
            Metrics = [CaseMetric("baseline", "0", 20, measured), CaseMetric("candidate", "0", 10, noObjects),
                CaseMetric("baseline", "1", 0) with { OmittedStatements = 1, StatementAttribution = "partial" },
                CaseMetric("candidate", "1", 0) with { StatementAttribution = "unavailable" }],
        };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "all", "sum", "statements");
        var row = Assert.Single(result.Statements);
        Assert.Equal("unmatched", row.Status);
        Assert.Null(row.ReadsBefore);
        Assert.Null(row.ReadsAfter);
        Assert.Null(Assert.Single(row.Objects).ReadsAfter);
        Assert.Equal("whole batch", result.VerdictBasis);
    }

    [Fact]
    public void Duplicate_occurrence_does_not_upgrade_verdict_or_show_counters()
    {
        var first = Statement("work", "SELECT work", 20, "p");
        var compare = Evidence(203, "h1", "h2", (20, 10)) with
        { Metrics = [StatementMetric("baseline", 0, 20, first, first), StatementMetric("candidate", 0, 10, first)] };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "0", "sum", "statements");
        Assert.Equal("whole batch", result.VerdictBasis);
        var row = Assert.Single(result.Statements);
        Assert.Null(row.ReadsBefore);
        Assert.Equal("ambiguous attribution", row.Availability!["reads"].Before.Reason);
    }

    [Fact]
    public void Duplicate_identity_does_not_prove_another_statement_absent()
    {
        var duplicate = Statement("dup", "SELECT duplicate", 10, "p");
        var other = Statement("other", "SELECT other", 5, "q");
        var compare = Evidence(203, "h1", "h2", (20, 5)) with
        { Metrics = [StatementMetric("baseline", 0, 20, duplicate, duplicate), StatementMetric("candidate", 0, 5, other)] };
        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "0", "sum", "statements");
        var row = Assert.Single(result.Statements, item => item.Key == "other");
        Assert.Null(row.ReadsBefore);
        Assert.Equal("unmatched", row.Status);
        Assert.Equal("unavailable", row.Availability!["reads"].Before.Status);
        Assert.Equal("whole batch", result.VerdictBasis);
    }

    [Fact]
    public void Execution_counts_are_independent_per_side_and_survive_old_evidence()
    {
        var before = Statement("work", "SELECT work", 20, "a") with { Executions = 2, RuntimeEvidenceEligible = false };
        var after = Statement("work", "SELECT work", 10, "b") with { Executions = 7 };
        var compare = Evidence(203, "h1", "h2", (20, 10)) with
        { Metrics = [StatementMetric("baseline", 0, 20, before), StatementMetric("candidate", 0, 10, after)] };
        StatementRow Run() => Assert.Single(OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "0", "sum", "statements").Statements);
        var row = Run();
        Assert.Equal(2, row.ExecutionsBefore);
        Assert.Equal(7, row.ExecutionsAfter);
        Assert.Equal("available", row.Availability!["executions"].Before.Status);
        Assert.Equal("available", row.Availability["executions"].After.Status);
        compare = compare with { Metrics = [compare.Metrics[0], compare.Metrics[1] with { Statements = [after with { Executions = null }] }] };
        row = Run();
        Assert.Equal(2, row.ExecutionsBefore);
        Assert.Null(row.ExecutionsAfter);
        Assert.Equal("unavailable", row.Availability!["executions"].After.Status);
    }

    [Fact]
    public void Unknown_object_kind_is_not_matched_by_name()
    {
        var row = Statement("work", "SELECT work", 20, "p") with { Objects = [new TableRow("Thing", 1, 1, 20, 20, "")] };
        var compare = Evidence(203, "h1", "h2", (20, 20)) with
        { Metrics = [StatementMetric("baseline", 0, 20, row), StatementMetric("candidate", 0, 20, row)] };
        var obj = Assert.Single(Assert.Single(OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "0", "sum", "statements").Statements).Objects);
        Assert.Null(obj.ReadsBefore);
        Assert.Equal("ambiguous attribution", obj.Availability!["reads"].Before.Reason);
    }

    [Fact]
    public void Equal_reads_with_different_plan_hashes_are_not_shared()
    {
        var compare = Evidence(203, "h1", "h2", (100, 100)) with
        {
            Metrics = [StatementMetric("baseline", 0, 100, Statement("same", "SELECT same", 100, "plan-a")),
                StatementMetric("candidate", 0, 100, Statement("same", "SELECT same", 100, "plan-b"))],
        };

        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "all", "sum", "whole");

        Assert.Equal("different-work", Assert.Single(result.Statements).Status);
        Assert.Contains("shared statements: 0", result.WholeBatch.Label);
        Assert.Equal("changed statements", result.VerdictBasis);
    }

    [Fact]
    public void Branch_statement_is_not_collapsed_when_missing_in_one_case()
    {
        var compare = Evidence(203, "h1", "h2", (100, 90), (120, 110)) with
        {
            Metrics = [
                CaseMetric("baseline", "0", 100, Statement("common", "SELECT common", 90, "common-plan"), Statement("branch", "SELECT branch", 10, "branch-plan")),
                CaseMetric("candidate", "0", 90, Statement("common", "SELECT common", 90, "common-plan"), Statement("branch", "SELECT branch", 0, "branch-plan")),
                CaseMetric("baseline", "1", 120, Statement("common", "SELECT common", 120, "common-plan")),
                CaseMetric("candidate", "1", 110, Statement("common", "SELECT common", 110, "common-plan")),
            ],
        };

        var result = Compare([compare], "v1", "v2");

        Assert.Equal("not-executed", Assert.Single(result.Statements, row => row.Key == "branch").Status);
        Assert.Equal("changed statements", result.VerdictBasis);
    }

    [Fact]
    public void Compared_statement_objects_join_by_name_and_report_the_side_to_side_change()
    {
        var before = Statement("work", "SELECT work", 20, "old-plan") with
        {
            Objects = [
                new TableRow("dbo.T", 2, 2, 20, 20, "0%") { ObjectKind = "table" },
                new TableRow("dbo.OldOnly", 1, 1, 5, 5, "0%") { ObjectKind = "table" },
            ],
        };
        var after = Statement("work", "SELECT work", 10, "new-plan") with
        {
            Objects = [
                new TableRow("dbo.T", 1, 1, 10, 10, "0%") { ObjectKind = "table" },
                new TableRow("dbo.NewOnly", 1, 1, 7, 7, "0%") { ObjectKind = "table" },
            ],
        };
        var compare = Evidence(203, "h1", "h2", (25, 17)) with
        {
            Metrics = [StatementMetric("baseline", 0, 25, before), StatementMetric("candidate", 0, 17, after)],
        };

        var result = Compare([compare], "v1", "v2");

        var objects = Assert.Single(result.Statements).Objects.ToDictionary(row => row.Table);
        var obj = objects["dbo.T"];
        Assert.Equal("dbo.T", obj.Table);
        Assert.Equal(2, obj.ScansBefore);
        Assert.Equal(1, obj.ScansAfter);
        Assert.Equal(20, obj.ReadsBefore);
        Assert.Equal(10, obj.ReadsAfter);
        Assert.Equal("−50%", obj.Change);
        Assert.Equal((long?)5, objects["dbo.OldOnly"].ReadsBefore);
        Assert.Equal((long?)0, objects["dbo.OldOnly"].ReadsAfter);
        Assert.Equal((long?)0, objects["dbo.NewOnly"].ReadsBefore);
        Assert.Equal((long?)7, objects["dbo.NewOnly"].ReadsAfter);
    }

    [Fact]
    public void All_case_branch_uses_confirmed_absence_as_zero_and_mean_includes_it()
    {
        var compare = Evidence(203, "h1", "h2", (20, 10), (0, 0)) with
        {
            Metrics = [
                CaseMetric("baseline", "0", 20, Statement("branch", "SELECT branch", 20, "before") with
                {
                    Objects = [new TableRow("dbo.T", 1, 1, 20, 20, "0%") { ObjectKind = "table" }],
                }),
                CaseMetric("candidate", "0", 10, Statement("branch", "SELECT branch", 10, "after") with
                {
                    Objects = [new TableRow("dbo.T", 1, 1, 10, 10, "0%") { ObjectKind = "table" }],
                }),
                CaseMetric("baseline", "1", 0),
                CaseMetric("candidate", "1", 0),
            ],
        };

        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "all", "mean", "statements");

        var branch = Assert.Single(result.Statements);
        Assert.Equal("not-executed", branch.Status);
        Assert.Equal(10, branch.ReadsBefore);
        Assert.Equal(5, branch.ReadsAfter);
        var obj = Assert.Single(branch.Objects);
        Assert.Equal(10, obj.ReadsBefore);
        Assert.Equal(5, obj.ReadsAfter);
    }

    [Fact]
    public void Missing_branch_with_unavailable_attribution_stays_null()
    {
        var compare = Evidence(203, "h1", "h2", (20, 10)) with
        {
            Metrics = [
                CaseMetric("baseline", "0", 20, Statement("branch", "SELECT branch", 20, "before")),
                CaseMetric("candidate", "0", 10) with { StatementAttribution = "unavailable" },
            ],
        };

        var result = Compare([compare], "v1", "v2");

        var branch = Assert.Single(result.Statements);
        Assert.Equal("unmatched", branch.Status);
        Assert.Null(branch.ReadsAfter);
        Assert.Equal("whole batch", result.VerdictBasis);
    }

    [Fact]
    public void Breakdown_is_additive_and_all_cases_share_only_when_each_case_matches()
    {
        var compare = Evidence(203, "h1", "h2", (100, 100), (200, 200)) with
        {
            Metrics = [
                CaseMetric("baseline", "0", 100, Statement("common", "SELECT common", 100, "common-plan")),
                CaseMetric("candidate", "0", 100, Statement("common", "SELECT common", 100, "common-plan")),
                CaseMetric("baseline", "1", 200, Statement("common", "SELECT common", 200, "common-plan")),
                CaseMetric("candidate", "1", 200, Statement("common", "SELECT common", 200, "common-plan")),
            ],
        };

        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "all", "sum", "whole");

        Assert.Equal(300, result.WholeBatch.Before);
        Assert.Equal(300, result.WholeBatch.SharedBefore);
        Assert.Equal(300, result.WholeBatch.SharedAfter);
        Assert.Equal(0, result.WholeBatch.ChangedBefore);
        Assert.Equal("shared", Assert.Single(result.Statements).Status);
        Assert.Equal("changed statements", result.VerdictBasis);
    }

    [Fact]
    public void Mean_breakdown_derives_changed_reads_from_rounded_whole_and_shared_totals()
    {
        var compare = Evidence(203, "h1", "h2", (100, 100), (100, 100)) with
        {
            Metrics = [
                CaseMetric("baseline", "0", 100, Statement("shared", "SELECT shared", 1, "shared-plan"), Statement("work", "SELECT work", 99, "work-plan")),
                CaseMetric("candidate", "0", 100, Statement("shared", "SELECT shared", 1, "shared-plan"), Statement("work", "SELECT work", 99, "new-work-plan")),
                CaseMetric("baseline", "1", 100, Statement("shared", "SELECT shared", 0, "shared-plan"), Statement("work", "SELECT work", 100, "work-plan")),
                CaseMetric("candidate", "1", 100, Statement("shared", "SELECT shared", 0, "shared-plan"), Statement("work", "SELECT work", 100, "new-work-plan")),
            ],
        };

        var result = OptimizationComparer.Compare(Group("h1", "h2"), [compare], "v1", "v2", "all", "mean", "whole");

        Assert.Equal(100, result.WholeBatch.Before);
        Assert.Equal(1, result.WholeBatch.SharedBefore);
        Assert.Equal(99, result.WholeBatch.ChangedBefore);
        Assert.Equal(result.WholeBatch.Before, result.WholeBatch.SharedBefore + result.WholeBatch.ChangedBefore);
        Assert.Equal(100, result.WholeBatch.After);
        Assert.Equal(result.WholeBatch.After, result.WholeBatch.SharedAfter + result.WholeBatch.ChangedAfter);
    }

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

    [Fact]
    public void Across_groups_pairs_only_varying_keys_and_never_claims_verdict()
    {
        var left = MeasureEvidence(215, "h0", 100) with
        {
            Cases = [new CompareCaseEvidence("0", "A", null, null, null) { VaryingFingerprint = "key" }],
            Metrics = [Metric("measure", "0", 100) with { RowsReturned = 1, RowsReturnedVaries = true }],
        };
        var right = MeasureEvidence(216, "h1", 50) with
        {
            Fingerprint = "other",
            Cases = [new CompareCaseEvidence("0", "B", null, null, null) { VaryingFingerprint = "key" }, new CompareCaseEvidence("1", "extra", null, null, null) { VaryingFingerprint = "other" }],
            Metrics = [Metric("measure", "0", 50) with { RowsReturned = 1 }, Metric("measure", "1", 30)],
        };
        var result = OptimizationComparer.CompareAcrossGroups(Group("h0"), Group("h1") with { Fingerprint = "other" }, [left, right], "v0", "v1", "all", "sum");
        Assert.Equal(PairBasis.ApproximateAcrossConditions, result.Basis);
        Assert.Equal("Different conditions — approximate, not a verdict", result.EquivalenceLabel);
        Assert.Equal("1 of 2 cases paired", result.CoverageLabel);
        Assert.DoesNotContain(result.Cases, c => c.Outcome is "better" or "worse");
        var rows = Assert.Single(result.CaseRows);
        Assert.Equal(("A", "B", 1L, 1L, true), (rows.LabelA, rows.LabelB, rows.RowsA, rows.RowsB, rows.RowsAVaries));
    }

    [Fact]
    public void Across_groups_pair_four_of_five_varying_cases()
    {
        var left = MeasureEvidence(215, "h0", 100) with
        {
            Cases = Enumerable.Range(0, 4).Select(i => new CompareCaseEvidence(i.ToString(), $"A{i}", null, null, null) { VaryingFingerprint = $"key{i}" }).ToArray(),
            Metrics = Enumerable.Range(0, 4).Select(i => Metric("measure", i.ToString(), 100)).ToArray()
        };
        var right = MeasureEvidence(216, "h1", 50) with
        {
            Fingerprint = "other",
            Cases = Enumerable.Range(0, 5).Select(i => new CompareCaseEvidence(i.ToString(), $"B{i}", null, null, null) { VaryingFingerprint = $"key{i}" }).ToArray(),
            Metrics = Enumerable.Range(0, 5).Select(i => Metric("measure", i.ToString(), 50)).ToArray()
        };
        var result = OptimizationComparer.CompareAcrossGroups(Group("h0"), Group("h1") with { Fingerprint = "other" }, [left, right], "v0", "v1", "all", "sum");
        Assert.Equal(4, result.Cases.Count);
        Assert.Equal("4 of 5 cases paired", result.CoverageLabel);
        Assert.Equal(400, result.WholeBatch.Before);
        Assert.Equal(200, result.WholeBatch.After);
    }

    [Fact]
    public void Across_groups_without_pair_keys_retains_snapshots_and_unknown_rows()
    {
        var left = MeasureEvidence(215, "h0", 100) with { Cases = [new CompareCaseEvidence("0", "same label", null, null, null) { MeasurementFingerprint = "left-key" }] };
        var right = MeasureEvidence(216, "h1", 50) with { Fingerprint = "other", Cases = [new CompareCaseEvidence("1", "same label", null, null, null) { MeasurementFingerprint = "right-key" }], Metrics = [Metric("measure", "1", 50)] };
        var result = OptimizationComparer.CompareAcrossGroups(Group("h0"), Group("h1") with { Fingerprint = "other" }, [left, right], "v0", "v1", "all", "sum");
        Assert.Empty(result.Cases);
        Assert.Empty(result.Tables);
        Assert.Null(result.WholeBatch.Before);
        Assert.Equal("0 of 2 cases paired", result.CoverageLabel);
        Assert.Equal(215, result.BeforeAction?.OperationId);
        Assert.Equal(216, result.AfterAction?.OperationId);
        Assert.Equal(["0"], result.BeforeAction?.CaseIds);
        Assert.Equal(["1"], result.AfterAction?.CaseIds);
        Assert.Equal("0", result.BeforeAction?.CaseIdsByFingerprint["left-key"]);
        Assert.Equal("1", result.AfterAction?.CaseIdsByFingerprint["right-key"]);
    }

    [Fact]
    public void Across_groups_rejects_ambiguous_keys_and_keeps_missing_rows_unknown()
    {
        var left = MeasureEvidence(215, "h0", 100) with
        {
            Cases = [new CompareCaseEvidence("0", "A", null, null, null) { VaryingFingerprint = "duplicate" },
                new CompareCaseEvidence("1", "B", null, null, null) { VaryingFingerprint = "duplicate" },
                new CompareCaseEvidence("2", "C", null, null, null) { VaryingFingerprint = "unique" }],
            Metrics = [Metric("measure", "0", 100)]
        };
        var right = MeasureEvidence(216, "h1", 50) with
        {
            Fingerprint = "other",
            Cases = [
            new CompareCaseEvidence("0", "A", null, null, null) { VaryingFingerprint = "duplicate" },
            new CompareCaseEvidence("2", "C", null, null, null) { VaryingFingerprint = "unique" }],
            Metrics = [Metric("measure", "0", 50)]
        };
        var result = OptimizationComparer.CompareAcrossGroups(Group("h0"), Group("h1") with { Fingerprint = "other" }, [left, right], "v0", "v1", "all", "sum");
        var row = Assert.Single(result.CaseRows);
        Assert.Equal("unique", row.CaseKey);
        Assert.Null(row.RowsA);
        Assert.Null(row.RowsB);
        Assert.False(row.RowsAVaries);
        Assert.False(row.RowsBVaries);
    }

    [Fact]
    public void Same_group_compare_preserves_existing_verdict_semantics()
    {
        var result = Compare([Evidence(214, "h1", "h2", (1000, 800))], "v1", "v2");
        Assert.Equal(PairBasis.MeasuredTogether, result.Basis);
        Assert.Equal(214, result.OperationId);
        Assert.Equal("results equivalent (direct)", result.EquivalenceLabel);
        Assert.Equal("better", Assert.Single(result.Cases).Outcome);
        Assert.Equal("whole batch", result.VerdictBasis);
        Assert.Equal(1000, Assert.Single(result.Tables).ReadsBefore);
        Assert.Equal(800, Assert.Single(result.Tables).ReadsAfter);
        Assert.Null(result.Elapsed.PolicyVerdict);
        Assert.Single(result.CaseRows);
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

    private static CompareMetricEvidence StatementMetric(string variant, long caseReads,
        long reads, params CompareStatementEvidence[] statements) =>
        CaseMetric(variant, "0", reads, statements);

    private static CompareMetricEvidence CaseMetric(string variant, string caseId,
        long reads, params CompareStatementEvidence[] statements) =>
        new(variant, caseId, $"@CustomerId = {9001 + int.Parse(caseId)}", reads, reads, 1,
            [new CompareTableIoEvidence("Orders", reads, 0, 1)])
        { Statements = statements, StatementReads = reads, StatementAttribution = "confirmed" };

    private static CompareStatementEvidence Statement(string key, string text, long reads, string planHash) =>
        new(key, 0, text, planHash, "confirmed", reads, 1, 1, [], []) { RuntimeEvidenceEligible = true };

    private static CompareMetricEvidence TableMetric(string variant, params (string Table, long Reads)[] tables) =>
        new(variant, "0", "@CustomerId = 9001", 100, 100, 1,
            tables.Select(table => new CompareTableIoEvidence(table.Table, table.Reads, 0, 1)).ToArray());
}
