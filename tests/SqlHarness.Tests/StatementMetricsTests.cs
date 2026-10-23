using System.Security;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests;

public sealed class StatementMetricsTests
{
    [Fact]
    public void Occurrence_distinguishes_repeated_text()
    {
        var sql = "INSERT INTO @P SELECT 1";
        var documents = new[]
        {
            JournalPlan(sql, statementId: 1, statementCompId: 4, queryPlanHash: "0x01", reads: 1, executions: 1),
            JournalPlan(sql, statementId: 2, statementCompId: 4, queryPlanHash: "0x01", reads: 1, executions: 1),
            JournalPlan(sql, statementId: 3, statementCompId: 5, queryPlanHash: "0x01", reads: 3, executions: 1),
        };

        var statements = JournalStatementMetricsExtractor.Extract(documents, out var omitted);

        Assert.Equal(0, omitted);
        Assert.Equal(2, statements.Count);
        Assert.Equal([2L, 1L], statements.Select(statement => statement.Executions).ToArray());
        Assert.Equal([0, 1], statements.Select(statement => statement.Occurrence).ToArray());
        Assert.Equal([2L, 3L], statements.Select(statement => Assert.Single(statement.Objects!).LogicalReads).ToArray());
        Assert.Equal(PlanIdentity.StatementSha256(StatementKey.Normalize(sql)), statements[0].Key);
        Assert.Equal(statements[0].Key, statements[1].Key);
    }

    [Fact]
    public void Counters_sum_operators_per_object()
    {
        var xml = JournalPlan("SELECT * FROM @P", 1, 1, "0x01", reads: 0, executions: 1,
            operators: [
                ("Table Insert", "[@P]", [(1001L, 0L, 0L)]),
                ("Table Scan", "[@P]", [(0L, 0L, 2L)]),
                ("Table Scan", "[@R]", [(4L, 2L, 1L)]),
                ("Table Scan", "[#F]", [(0L, 0L, 2L)])]);

        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract([xml], out _));

        var tableVariable = Assert.Single(statement.Objects!, item => item.Object == "[@P]");
        Assert.Equal((1001L, 0L, 2L), (tableVariable.LogicalReads, tableVariable.LobLogicalReads, tableVariable.Scans));
        Assert.Equal(2, Assert.Single(statement.Objects!, item => item.Object == "[#F]").Scans);
        var secondTableVariable = Assert.Single(statement.Objects!, item => item.Object == "[@R]");
        Assert.Equal("table-variable", secondTableVariable.Kind);
        Assert.Equal((4L, 2L, 1L), (secondTableVariable.LogicalReads, secondTableVariable.LobLogicalReads, secondTableVariable.Scans));
        Assert.Equal("temp", Assert.Single(statement.Objects!, item => item.Object == "[#F]").Kind);

    }

    [Fact]
    public void Parallel_counters_sum_over_threads()
    {
        var parallel = JournalPlan("SELECT * FROM [dbo].[T]", 1, 1, "0x02", 0, 1,
            operators: [("Index Scan", "[dbo].[T]", [(41943L, 2L, 1L), (42810L, 3L, 1L), (2L, 4L, 1L)])]);

        var parallelStatement = Assert.Single(JournalStatementMetricsExtractor.Extract([parallel], out _));
        var objectCounters = Assert.Single(parallelStatement.Objects!);
        Assert.Equal(84755, objectCounters.LogicalReads);
        Assert.Equal(9, objectCounters.LobLogicalReads);
        Assert.Equal(3, objectCounters.Scans);
        Assert.Equal(2, parallelStatement.DegreeOfParallelism);
    }

    [Fact]
    public void Missing_root_execution_count_does_not_make_complete_object_io_incomplete()
    {
        var xml = JournalPlan("SELECT * FROM [dbo].[T]", 1, 1, "0x01", reads: 7, executions: 1)
            .Replace(
                "ActualRows=\"1\" ActualExecutions=\"1\" />",
                "ActualRows=\"1\" ActualLogicalReads=\"0\" ActualLobLogicalReads=\"0\" ActualScans=\"0\" />",
                StringComparison.Ordinal);

        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract([xml], out _));

        Assert.Null(statement.Executions);
        Assert.True(statement.CountersComplete);
        var counters = Assert.Single(statement.Objects!);
        Assert.Equal(7, counters.LogicalReads);
    }

    [Fact]
    public void Table_variable_names_come_from_plan()
    {
        var xml = JournalPlan("SELECT * FROM @P JOIN @R ON 1 = 1", 1, 1, "0x01", 0, 1,
            operators: [("Table Scan", "[@P]", [(1L, 0L, 1L)]), ("Table Scan", "[@R]", [(2L, 0L, 1L)])]);

        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract([xml], out _));

        Assert.Equal(["[@P]", "[@R]"], statement.Objects!.Select(item => item.Object).ToArray());
        Assert.All(statement.Objects!, item => Assert.Equal("table-variable", item.Kind));
    }

    [Fact]
    public void Journal_extraction_preserves_actual_cursor_and_spool_document_shapes()
    {
        var cursorOpen = ReadPlanFixture("082-cursor-open.sqlplan");
        var cursorFetch = ReadPlanFixture("082-cursor-fetch.sqlplan");
        var spool = ReadPlanFixture("082-recursive-spool.sqlplan");

        var statements = JournalStatementMetricsExtractor.Extract([cursorOpen, cursorFetch, spool], out _);

        Assert.Equal(3, statements.Count);
        Assert.Equal(1, statements[0].Executions);
        Assert.Equal(1, statements[1].Executions);
        Assert.Equal(1, statements[2].Executions);
        Assert.Contains(statements[0].Objects!, item => item.Object == "[@t]");
        Assert.Empty(statements[2].Objects!);
        Assert.False(statements[2].CountersComplete);
    }

    [Fact]
    public void Object_free_non_io_operators_without_runtime_counters_do_not_block_object_reconciliation()
    {
        var cases = new[]
        {
            (
                new[]
                {
                    ReadPlanFixture("082-scalar-baseline-setup.sqlplan"),
                    ReadPlanFixture("082-scalar-baseline.sqlplan"),
                },
                new[]
                {
                    new TableIoCounters("table variables", 2, 6029, 0, 0, 0, 0, 0, 0),
                    new TableIoCounters("#p082_seed_1019", 1, 3000, 0, 0, 0, 0, 0, 0),
                }),
            (
                new[]
                {
                    ReadPlanFixture("082-scalar-candidate-setup.sqlplan"),
                    ReadPlanFixture("082-scalar-candidate.sqlplan"),
                },
                new[]
                {
                    new TableIoCounters("table variables", 1, 6019, 0, 0, 0, 0, 0, 0),
                    new TableIoCounters("#p082_seed_1019", 1, 3000, 0, 0, 0, 0, 0, 0),
                }),
        };

        foreach (var (plans, observed) in cases)
        {
            var statements = JournalStatementMetricsExtractor.Extract(plans, out var omitted);

            Assert.Equal(0, omitted);
            Assert.Equal([StatementStatus.Confirmed, StatementStatus.Confirmed],
                StatementReconciler.Reconcile(statements, observed));
        }
    }

    [Fact]
    public void Missing_io_counters_on_other_object_free_operators_remain_incomplete()
    {
        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract(
            [NoObjectPlan("Filter", "ActualExecutions=\"1\"")], out _));

        Assert.False(statement.CountersComplete);
    }

    [Theory]
    [InlineData("Compute Scalar")]
    [InlineData("Nested Loops")]
    [InlineData("Stream Aggregate")]
    public void Positive_io_on_whitelisted_object_free_operators_remains_incomplete(string physicalOp)
    {
        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract(
            [NoObjectPlan(physicalOp, "ActualExecutions=\"1\" ActualLogicalReads=\"1\" ActualLobLogicalReads=\"0\" ActualScans=\"0\"")], out _));

        Assert.False(statement.CountersComplete);
    }

    [Fact]
    public void Journal_extraction_does_not_group_missing_or_conflicting_compiled_identities()
    {
        var sql = "SELECT * FROM [dbo].[T]";
        var documents = new[]
        {
            JournalPlan(sql, 1, 0, "0x01", 1, 1),
            JournalPlan(sql, 2, 0, "0x01", 1, 1),
            JournalPlan(sql, 3, 7, "0x01", 1, 1),
            JournalPlan(sql, 4, 7, "0x02", 1, 1),
        };

        var statements = JournalStatementMetricsExtractor.Extract(documents, out _);

        Assert.Equal(4, statements.Count);
        Assert.All(statements, statement => Assert.Equal(1, statement.Executions));
        Assert.All(statements, statement => Assert.False(statement.IdentityComplete));
        Assert.Equal([0, 1, 2, 3], statements.Select(statement => statement.Occurrence).ToArray());
    }

    [Fact]
    public void Scalar_udf_share()
    {
        var udf = JournalPlan("SELECT dbo.fn_scalar()", 1, 1, "0x01", 0, 1,
            udfElapsedTime: 28, elapsedTime: 29,
            operators: [("Compute Scalar", "", [(0L, 0L, 0L)])]);
        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract([udf], out _));

        Assert.Equal(28, statement.UdfElapsedMs);
        var scalarUdf = Assert.Single(statement.HiddenWork!);
        Assert.Equal("scalar-udf", scalarUdf.Kind);
        Assert.Equal(96.6, scalarUdf.SharePercent!.Value, precision: 1);
    }

    [Fact]
    public void Reconciled_scalar_udf_counter_attribution_is_complete_with_hidden_work_reported_separately()
    {
        var udf = JournalPlan("SELECT dbo.fn_scalar()", 1, 1, "0x01", 0, 1,
            udfElapsedTime: 28, elapsedTime: 29,
            operators: [("Compute Scalar", "", [(0L, 0L, 0L)])]);
        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract([udf], out _));
        var status = Assert.Single(StatementReconciler.Reconcile(
            [statement], [new TableIoCounters("", 0, 0, 0, 0, 0, 0, 0, 0)]));

        Assert.Equal(StatementCounterAttribution.Complete, StatementAttributionClassifier.Classify(status));
        Assert.Single(statement.HiddenWork!);
    }

    [Fact]
    public void Compact_statement_evidence_keeps_udf_limitation_when_counters_are_complete()
    {
        var udf = JournalPlan("SELECT dbo.fn_scalar()", 1, 1, "0x01", 0, 1,
            udfElapsedTime: 28, elapsedTime: 29,
            operators: [("Compute Scalar", "", [(0L, 0L, 0L)])]);
        var run = new CompareRunArtifact("candidate", 1, 0, 29, 0,
            new Dictionary<string, long>(), "result", [udf], 1)
        {
            TableIo = [new TableIoCounters("", 0, 0, 0, 0, 0, 0, 0, 0)],
        };

        var evidence = AgentStatementEvidenceProjection.Create(ArtifactReader.CompareKind, [run]);

        Assert.Equal("available", evidence.EvidenceAvailability);
        var candidate = Assert.Single(evidence.Cases);
        Assert.Equal("complete", candidate.CounterAttribution);
        Assert.Contains(candidate.Limitations, limitation => limitation.Contains("Scalar UDF", StringComparison.Ordinal));
    }

    [Fact]
    public void Compact_statement_evidence_marks_missing_repetition_and_entire_case()
    {
        var plan = JournalPlan("SELECT dbo.fn_scalar()", 1, 1, "0x01", 0, 1,
            udfElapsedTime: 28, elapsedTime: 29,
            operators: [("Compute Scalar", "", [(0L, 0L, 0L)])]);
        CompareRunArtifact CompleteRun(string variant, int repetition) => new(
            variant, repetition, 0, 29, 0, new Dictionary<string, long>(), "result", [plan], 1)
        {
            TableIo = [new TableIoCounters("", 0, 0, 0, 0, 0, 0, 0, 0)],
        };
        CompareRunArtifact MissingRun(string variant, int repetition) => new(
            variant, repetition, 0, 0, 0, new Dictionary<string, long>(), "result", [], 1);

        var evidence = AgentStatementEvidenceProjection.Create(ArtifactReader.CompareKind,
        [
            CompleteRun("candidate", 1),
            MissingRun("candidate", 2),
            MissingRun("baseline", 1),
        ]);

        var candidate = Assert.Single(evidence.Cases, item => item.Variant == "candidate");
        Assert.Equal("partial", candidate.CounterAttribution);
        Assert.Contains(candidate.Limitations,
            limitation => limitation.Contains("statement evidence is missing for 1 of 2 runs", StringComparison.OrdinalIgnoreCase));

        var baseline = Assert.Single(evidence.Cases, item => item.Variant == "baseline");
        Assert.Equal("unavailable", baseline.CounterAttribution);
        Assert.Contains(baseline.Limitations,
            limitation => limitation.Contains("statement evidence is missing for 1 of 1 runs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compact_statement_evidence_scopes_omissions_to_the_affected_case()
    {
        var plan = JournalPlan("SELECT 1", 1, 1, "0x01", 0, 1);
        CompareRunArtifact Run(string variant, int repetition, IReadOnlyList<string> plans) => new(
            variant, repetition, 0, 1, 0, new Dictionary<string, long>(), "result", plans, 1)
        {
            TableIo = [new TableIoCounters("[@P]", 0, 0, 0, 0, 0, 0, 0, 0)],
        };
        var many = Enumerable.Range(0, 513)
            .Select(index => JournalPlan($"SELECT {index}", index + 1, index + 1, $"0x{index + 1:X}", 0, 1))
            .ToArray();
        var evidence = AgentStatementEvidenceProjection.Create(ArtifactReader.CompareKind,
        [
            Run("candidate", 1, [plan]),
            Run("baseline", 1, many),
            Run("unavailable", 1, []),
        ]);

        Assert.Equal("complete", Assert.Single(evidence.Cases, item => item.Variant == "candidate").CounterAttribution);
        Assert.Equal("partial", Assert.Single(evidence.Cases, item => item.Variant == "baseline").CounterAttribution);
        Assert.Equal("unavailable", Assert.Single(evidence.Cases, item => item.Variant == "unavailable").CounterAttribution);
    }

    [Fact]
    public void Missing_statement_attribution_is_unavailable_and_legacy_artifact_shape_stays_unchanged()
    {
        Assert.Equal(
            StatementCounterAttribution.Unavailable,
            StatementAttributionClassifier.Classify(null));

        var legacy = StatementMetricsExtractor.Extract(CollidingNodeIdPlan(), out _).First();
        var json = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("\"attribution\"", json);
        Assert.DoesNotContain("\"hiddenWork\"", json);

        var agent = new AgentBenchmarkVariant(null, null, null,
            new Dictionary<string, AgentMetricDistribution>(), [],
            BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Unavailable);
        var agentJson = JsonSerializer.Serialize(agent, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"attribution\"", agentJson);
        Assert.DoesNotContain("\"hiddenWork\"", agentJson);
    }

    [Fact]
    public void Multi_statement_tvf_flagged()
    {
        var tvf = JournalPlan("SELECT * FROM dbo.fn_multi()", 2, 2, "0x02", 0, 1,
            operators: [("Table-valued function", "[dbo].[fn_multi]", [(0L, 0L, 0L)])]);

        var statement = Assert.Single(JournalStatementMetricsExtractor.Extract([tvf], out _));

        var hidden = Assert.Single(statement.HiddenWork!);
        Assert.Equal("multi-statement-tvf", hidden.Kind);
        Assert.Equal("[dbo].[fn_multi]", hidden.Name);
        Assert.Equal(1, hidden.Executions);
    }

    [Fact]
    public void Normalized_key_ignores_comments_and_whitespace()
    {
        Assert.Equal(
            "SELECT 'a  -- b' FROM [dbo].[T]",
            StatementKey.Normalize("  SELECT  'a  -- b' /* removable */\r\n FROM [dbo].[T] -- trailing\n"));
        Assert.Equal(
            StatementKey.Normalize("SELECT 'x  y'"),
            StatementKey.Normalize("SELECT 'x  y' -- comment"));
        Assert.NotEqual(StatementKey.Normalize("SELECT 'x  y'"), StatementKey.Normalize("SELECT 'x y'"));
    }

    [Fact]
    public void Large_batch_reports_omissions()
    {
        var largeText = new string('x', 20_000);
        var documents = Enumerable.Range(1, 300)
            .Select(index => JournalPlan($"SELECT '{largeText}' AS value_{index}", index, index, $"0x{index:X}", 0, 1))
            .ToArray();

        var statements = JournalStatementMetricsExtractor.Extract(documents, out var omitted);

        Assert.InRange(statements.Count, 1, 299);
        Assert.True(omitted > 0);
        Assert.Equal(300, statements.Count + omitted);
    }

    [Fact]
    public void Legacy_artifact_statement_cap_and_wire_shape_remain_unchanged()
    {
        var statementsXml = string.Join("", Enumerable.Range(0, 300)
            .Select(index => $"<StmtSimple StatementText=\"SELECT {index}\"><QueryPlan /></StmtSimple>"));
        var xml = $"<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>{statementsXml}</Statements></Batch></BatchSequence></ShowPlanXML>";

        var artifactStatements = StatementMetricsExtractor.Extract(xml, out var artifactOmitted);
        var oneMetricJson = JsonSerializer.Serialize(
            Assert.Single(StatementMetricsExtractor.Extract(JournalPlan("SELECT 1", 1, 1, "0x01", 0, 1), out _)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(128, StatementMetricsExtractor.MaximumStatements);
        Assert.Equal(128, artifactStatements.Count);
        Assert.Equal(172, artifactOmitted);
        Assert.DoesNotContain("\"key\"", oneMetricJson);
        Assert.DoesNotContain("\"objects\"", oneMetricJson);
        Assert.DoesNotContain("\"hiddenWork\"", oneMetricJson);
    }

    [Fact]
    public void Extract_keeps_colliding_node_ids_on_separate_statements_with_preorder_paths()
    {
        var statements = StatementMetricsExtractor.Extract(CollidingNodeIdPlan(), out var omitted);

        Assert.Equal(0, omitted);
        Assert.Equal(2, statements.Count);
        Assert.Equal(0, statements[0].StatementOrdinal);
        Assert.Equal(1, statements[1].StatementOrdinal);
        Assert.Equal(1, Assert.Single(statements[0].TopOperators).NodeId);
        Assert.Equal(1, Assert.Single(statements[1].TopOperators).NodeId);
        Assert.Equal("0", statements[0].TopOperators[0].NodePath);
        Assert.Equal("0", statements[1].TopOperators[0].NodePath);
        Assert.Equal(0, statements[0].TopOperators[0].Cursor);
        Assert.Equal(0, statements[1].TopOperators[0].Cursor);
        Assert.NotEqual(statements[0].StatementHash, statements[1].StatementHash);
    }

    [Fact]
    public void Extract_node_paths_match_distilled_preorder_including_nested_children()
    {
        var xml = NestedRelOpPlan();
        var extracted = Assert.Single(StatementMetricsExtractor.Extract(xml, out _));
        var distilled = Assert.Single(PlanDistiller.Distill(xml).Statements);
        var preorder = PlanDistiller.Preorder(distilled.Root).ToArray();

        Assert.Equal(3, extracted.TopOperators.Count);
        Assert.Equal(["0", "0/0", "0/1"], extracted.TopOperators.Select(op => op.NodePath!).ToArray());
        Assert.Equal([0, 1, 2], extracted.TopOperators.Select(op => op.Cursor!.Value).ToArray());
        Assert.Equal(preorder.Select(node => node.Path).ToArray(), extracted.TopOperators.Select(op => op.NodePath!).ToArray());
        Assert.Equal([0, 1, 2], preorder.Select(node => node.Node.NodeId!.Value).ToArray());
    }

    [Fact]
    public void Execution_plan_parser_attaches_statement_path_and_cursor_instead_of_flattening_colliding_ids()
    {
        var plan = ExecutionPlanParser.Parse(CollidingNodeIdPlan());

        Assert.Equal(2, plan.StatementCount);
        Assert.Equal(2, plan.Operators.Count);
        Assert.Equal([0, 1], plan.Operators.Select(op => op.StatementOrdinal).ToArray());
        Assert.Equal([1, 1], plan.Operators.Select(op => op.NodeId).ToArray());
        Assert.Equal(["0", "0"], plan.Operators.Select(op => op.NodePath!).ToArray());
        Assert.Equal([0, 0], plan.Operators.Select(op => op.Cursor!.Value).ToArray());
        Assert.Equal("Index Seek", plan.Operators[0].PhysicalOp);
        Assert.True(plan.Operators[1].HasSpill);
    }

    [Fact]
    public void Variant_report_preserves_origin_across_repetitions_and_does_not_merge_colliding_ids()
    {
        var xml = CollidingNodeIdPlan();
        var runs = new[]
        {
            MeasuredRun("baseline", 1, xml),
            MeasuredRun("baseline", 2, xml),
        };

        var operators = BenchmarkReports.CreateVariantReport("baseline", runs).Operators;

        Assert.Equal(4, operators.Count);
        Assert.All(operators, op =>
        {
            Assert.NotNull(op.NodePath);
            Assert.NotNull(op.Statement);
            Assert.NotNull(op.Repetition);
            Assert.NotNull(op.Cursor);
        });
        Assert.Equal(2, operators.Count(op => op.Statement == 1 && op.HasSpill));
        Assert.Contains(operators, op => op.Statement == 0 && op.Repetition == 1 && op.NodeId == 1 && !op.HasSpill);
        Assert.Contains(operators, op => op.Statement == 1 && op.Repetition == 2 && op.NodeId == 1 && op.HasSpill);
    }

    [Fact]
    public void Statement_metrics_projection_preserves_node_paths_for_each_repetition()
    {
        var xml = NestedRelOpPlan();
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 10, 11, 1, new Dictionary<string, long>(), "h1", [xml], 0),
            new CompareRunArtifact("baseline", 2, 20, 21, 2, new Dictionary<string, long>(), "h2", [xml], 0),
        };

        var section = ArtifactStatementProjector.Project(ArtifactReader.CompareKind, runs);
        Assert.Equal([1, 2], section.Statements.Select(statement => statement.Repetition).ToArray());
        Assert.All(section.Statements, statement =>
        {
            var operators = statement.TopOperators.ToDictionary(op => op.NodeId);
            Assert.Equal(0, statement.StatementOrdinal);
            Assert.Equal("0", operators[0].NodePath);
            Assert.Equal("0/0", operators[1].NodePath);
            Assert.Equal("0/1", operators[2].NodePath);
            Assert.Equal(1, operators[1].Cursor);
        });
    }

    [Fact]
    public void Statement_metrics_hash_uses_the_paired_full_executed_span()
    {
        var sql = "SELECT N'" + new string('a', 4100) + "';";
        var statementText = System.Security.SecurityElement.Escape(sql[..3999]);
        var xml = $"<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements><StmtSimple StatementText=\"{statementText}\"><QueryPlan /></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>";
        var run = new CompareRunArtifact("baseline", 1, 10, 11, 1, new Dictionary<string, long>(), "h", [xml], 0)
        {
            ExecutedSql = sql,
        };

        var statement = Assert.Single(ArtifactStatementProjector.Project(ArtifactReader.CompareKind, [run]).Statements);
        var journalStatement = Assert.Single(JournalBenchmarkBuilder.Build([run]).Statements);

        Assert.Equal(PlanIdentity.StatementSha256(sql), statement.StatementHash);
        Assert.NotEqual(PlanIdentity.StatementSha256(sql[..3999]), statement.StatementHash);
        Assert.Equal(statement.StatementHash, journalStatement.StatementHash);
    }

    [Fact]
    public void Journal_statement_hash_keeps_plan_text_when_executed_span_is_unmatched()
    {
        var planText = "SELECT 1";
        var run = new CompareRunArtifact(
            "baseline", 1, 10, 11, 1, new Dictionary<string, long>(), "h",
            [JournalPlan(planText, 1, 1, "0x01", 0, 1)], 0)
        {
            ExecutedSql = "SELECT 2",
        };

        var journalStatement = Assert.Single(JournalBenchmarkBuilder.Build([run]).Statements);

        Assert.Equal(PlanIdentity.StatementSha256(planText), journalStatement.StatementHash);
        Assert.NotEqual(PlanIdentity.StatementSha256(run.ExecutedSql), journalStatement.StatementHash);
    }

    [Fact]
    public void Postgres_explain_json_is_readable_without_node_ids()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seq-scan.explain.json"));
        var distilled = PostgresPlanDistiller.Distill(json);
        var parsed = ExecutionPlanParser.Parse(json);

        Assert.All(PlanDistiller.Preorder(distilled.Statements[0].Root), node => Assert.Null(node.Node.NodeId));
        Assert.NotEmpty(parsed.Operators);
        Assert.Equal("0", parsed.Operators[0].NodePath);
        var stored = StoredPlanReader.Read(JsonSerializer.Serialize(distilled, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Null(stored.Statements[0].Root.NodeId);
    }

    internal static CollectedBenchmarkRun MeasuredRun(string variant, int repetition, string xml, string? parameterSet = null)
    {
        var artifact = new CompareRunArtifact(
            variant, repetition, 1, 2, 3, new Dictionary<string, long>(), "hash", [xml], 0, parameterSet);
        var parsed = ExecutionPlanParser.Parse(xml);
        return new CollectedBenchmarkRun(artifact, [parsed], ["hash"]);
    }

    internal static string CollidingNodeIdPlan() => """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT a FROM dbo.Accounts">
              <QueryPlan>
                <RelOp NodeId="1" PhysicalOp="Index Seek" EstimateRows="1">
                  <IndexScan><Object Table="[dbo].[Accounts]" /></IndexScan>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
            <StmtSimple StatementText="SELECT b FROM dbo.Accounts">
              <QueryPlan>
                <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" EstimateRows="7">
                  <Warnings><SpillToTempDb SpillLevel="1" /></Warnings>
                  <IndexScan><Object Table="[dbo].[Accounts]" /></IndexScan>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    internal static string NestedRelOpPlan() => """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT nested">
              <QueryPlan>
                <RelOp NodeId="0" PhysicalOp="Nested Loops" EstimateRows="2">
                  <NestedLoops>
                    <RelOp NodeId="1" PhysicalOp="Index Seek" EstimateRows="3">
                      <IndexScan><Object Table="[dbo].[Child]" Index="[IX_Child]" /></IndexScan>
                    </RelOp>
                    <RelOp NodeId="2" PhysicalOp="Table Scan" EstimateRows="4">
                      <Warnings><SpillToTempDb SpillLevel="1" /></Warnings>
                      <TableScan><Object Table="[dbo].[Parent]" /></TableScan>
                    </RelOp>
                  </NestedLoops>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    private static string ReadPlanFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Plans", name));

    private static string NoObjectPlan(string physicalOp, string counterAttributes) => $"""
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT 1" StatementId="1" StatementCompId="1" QueryPlanHash="0x01">
              <QueryPlan>
                <RelOp NodeId="0" PhysicalOp="{SecurityElement.Escape(physicalOp)}" LogicalOp="{SecurityElement.Escape(physicalOp)}">
                  <RunTimeInformation><RunTimeCountersPerThread Thread="0" {counterAttributes} /></RunTimeInformation>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    internal static string JournalPlan(
        string text,
        int statementId,
        int statementCompId,
        string queryPlanHash,
        long reads,
        long executions,
        long? udfElapsedTime = null,
        long elapsedTime = 29,
        (string PhysicalOp, string Table, (long Reads, long LobReads, long Scans)[] Threads)[]? operators = null)
    {
        operators ??= [("Table Scan", "[@P]", [(reads, 0L, 0L)])];
        var escapedText = SecurityElement.Escape(text);
        var operatorsXml = string.Join("", operators.Select((op, index) => $"""
            <RelOp NodeId="{index}" PhysicalOp="{SecurityElement.Escape(op.PhysicalOp)}" LogicalOp="{SecurityElement.Escape(op.PhysicalOp)}" EstimateRows="1">
              <RunTimeInformation>{string.Join("", op.Threads.Select((thread, threadId) => $"""
                <RunTimeCountersPerThread Thread="{threadId}" ActualRows="1" ActualExecutions="{executions}" ActualLogicalReads="{thread.Reads}" ActualLobLogicalReads="{thread.LobReads}" ActualScans="{thread.Scans}" />
              """))}</RunTimeInformation>
              <TableScan><Object Table="{SecurityElement.Escape(op.Table)}" /></TableScan>
            </RelOp>
            """));
        var udfAttribute = udfElapsedTime is null ? string.Empty : $" UdfElapsedTime=\"{udfElapsedTime.Value}\"";
        var rootExecutions = executions;
        return $"""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementText="{escapedText}" StatementId="{statementId}" StatementCompId="{statementCompId}" StatementType="SELECT" QueryHash="0x01" QueryPlanHash="{queryPlanHash}">
                  <QueryPlan DegreeOfParallelism="2"><QueryTimeStats CpuTime="1" ElapsedTime="{elapsedTime}"{udfAttribute} />
                    <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="1">
                      <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="1" ActualExecutions="{rootExecutions}" /></RunTimeInformation>
                      <NestedLoops>{operatorsXml}</NestedLoops>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """;
    }
}