using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class PlanMetricsExtractorTests
{
    internal const string ActualPlan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.564" Build="16.0.1000.6">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="SELECT * FROM dbo.Orders WHERE Note = 'SQLH_SQL_MARKER'" StatementType="SELECT">
              <QueryPlan DegreeOfParallelism="4" MemoryGrant="4096" CachedPlanSize="40" CompileTime="12" CompileCPU="10" CompileMemory="300">
                <MissingIndexes>
                  <MissingIndexGroup Impact="80.5">
                    <MissingIndex Database="[db]" Schema="[dbo]" Table="[Orders]">
                      <ColumnGroup Usage="EQUALITY"><Column Name="[CustomerId]" ColumnId="2" /></ColumnGroup>
                    </MissingIndex>
                  </MissingIndexGroup>
                </MissingIndexes>
                <MemoryGrantInfo SerialRequiredMemory="512" SerialDesiredMemory="1024" RequiredMemory="1024" DesiredMemory="4096" RequestedMemory="4096" GrantWaitTime="0" GrantedMemory="4096" MaxUsedMemory="256" MaxQueryMemory="100000" />
                <WaitStats>
                  <Wait WaitType="PAGEIOLATCH_SH" WaitTimeMs="40" WaitCount="7" />
                  <Wait WaitType="CXPACKET" WaitTimeMs="15" WaitCount="3" />
                </WaitStats>
                <QueryTimeStats CpuTime="30" ElapsedTime="80" />
                <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="Sort">
                  <Warnings><SpillToTempDb SpillLevel="1" SpilledThreadCount="1" /></Warnings>
                  <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan">
                    <Warnings><PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(nvarchar(50),[Note],0)" /></Warnings>
                  </RelOp>
                </RelOp>
                <ParameterList><ColumnReference Column="@p" ParameterCompiledValue="N'SQLH_PARAM_MARKER'" /></ParameterList>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    internal const string ExplainJson = """
        [{ "Plan": { "Node Type": "Gather", "Workers Launched": 2,
                     "Shared Hit Blocks": 100, "Shared Read Blocks": 20, "Shared Dirtied Blocks": 1,
                     "Shared Written Blocks": 0, "Temp Read Blocks": 5, "Temp Written Blocks": 6,
                     "Plans": [ { "Node Type": "Sort", "Sort Space Type": "Disk",
                                  "Plans": [ { "Node Type": "Hash", "Hash Batches": 4 } ] } ] },
           "Planning Time": 0.5, "Execution Time": 12.0 }]
        """;

    [Fact]
    public void Showplan_metrics_are_extracted()
    {
        var metrics = PlanMetricsExtractor.Extract(ActualPlan);

        Assert.Equal(4096, metrics.GrantRequestedKb);
        Assert.Equal(4096, metrics.GrantGrantedKb);
        Assert.Equal(256, metrics.GrantMaxUsedKb);
        Assert.Equal(4, metrics.Dop);
        Assert.Equal(12, metrics.CompileTimeMs);
        Assert.Equal(10, metrics.CompileCpuMs);
        Assert.Equal(1, metrics.SpillCount);
        Assert.True(metrics.HasWarnings);
        Assert.True(metrics.HasImplicitConversion);
        Assert.Equal(1, metrics.MissingIndexCount);
        Assert.Equal([new PlanWait("PAGEIOLATCH_SH", 40, 7), new PlanWait("CXPACKET", 15, 3)], metrics.Waits);
        Assert.Null(metrics.Postgres);
    }

    [Fact]
    public void Extracted_metrics_carry_no_statement_text_or_parameter_values()
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(PlanMetricsExtractor.Extract(ActualPlan));

        Assert.DoesNotContain("SQLH_SQL_MARKER", serialized);
        Assert.DoesNotContain("SQLH_PARAM_MARKER", serialized);
        Assert.DoesNotContain("CONVERT_IMPLICIT", serialized);
    }

    [Fact]
    public void Explain_json_metrics_are_extracted()
    {
        var metrics = PlanMetricsExtractor.Extract(ExplainJson);

        Assert.Equal(new PostgresBufferCounters(100, 20, 1, 0, 5, 6), metrics.Postgres);
        Assert.Equal(3, metrics.Dop);
        Assert.Equal(2, metrics.SpillCount);
        Assert.Null(metrics.GrantGrantedKb);
        Assert.Empty(metrics.Waits);
    }

    [Fact]
    public void Multiple_documents_are_combined()
    {
        var metrics = PlanMetricsExtractor.Extract([ActualPlan, ActualPlan]);

        Assert.Equal(8192, metrics.GrantGrantedKb);
        Assert.Equal(4, metrics.Dop);
        Assert.Equal(2, metrics.SpillCount);
        Assert.Equal(2, metrics.MissingIndexCount);
        Assert.Equal(new PlanWait("PAGEIOLATCH_SH", 80, 14), metrics.Waits[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<ShowPlanXML")]
    [InlineData("{ not json")]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"boom\">]><ShowPlanXML>&e;</ShowPlanXML>")]
    public void Malformed_or_dtd_plans_yield_empty_metrics(string document) =>
        Assert.Equal(PlanMetrics.Empty, PlanMetricsExtractor.Extract(document));

    [Fact]
    public void Oversized_plan_yields_the_empty_metrics_instance()
    {
        // Trailing whitespace keeps the XML well formed, so only the size cap can reject it.
        var oversized = ActualPlan + new string(' ', PlanMetricsExtractor.MaximumCharacters - ActualPlan.Length + 1);

        Assert.NotSame(PlanMetrics.Empty, PlanMetricsExtractor.Extract(ActualPlan));
        Assert.Same(PlanMetrics.Empty, PlanMetricsExtractor.Extract(oversized));
    }

    [Fact]
    public void Plan_without_runtime_elements_has_null_grant_and_no_waits()
    {
        const string estimated = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><RelOp NodeId="0" PhysicalOp="Index Seek" /></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

        var metrics = PlanMetricsExtractor.Extract(estimated);

        Assert.Null(metrics.GrantGrantedKb);
        Assert.Null(metrics.Dop);
        Assert.Empty(metrics.Waits);
        Assert.Equal(0, metrics.SpillCount);
    }

    [Fact]
    public void Combining_huge_values_never_throws()
    {
        const string hugeWait = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><WaitStats><Wait WaitType="X" WaitTimeMs="9223372036854775807" WaitCount="9223372036854775807" /></WaitStats></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";
        const string hugeBuffers = """[{ "Plan": { "Node Type": "Seq Scan", "Shared Hit Blocks": 9223372036854775807, "Shared Read Blocks": 0, "Shared Dirtied Blocks": 0, "Shared Written Blocks": 0, "Temp Read Blocks": 0, "Temp Written Blocks": 0 } }]""";

        var waits = Record.Exception(() => PlanMetricsExtractor.Extract([hugeWait, hugeWait]));
        var buffers = Record.Exception(() => PlanMetricsExtractor.Extract([hugeBuffers, hugeBuffers]));

        Assert.Null(waits);
        Assert.Null(buffers);
    }

    [Fact]
    public void Convert_implicit_in_statement_text_or_parameters_is_not_a_conversion()
    {
        const string plan = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT 1 /* CONVERT_IMPLICIT */"><QueryPlan><RelOp NodeId="0" PhysicalOp="Index Seek" /><ParameterList><ColumnReference Column="@p" ParameterCompiledValue="N'CONVERT_IMPLICIT'" /></ParameterList></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

        Assert.False(PlanMetricsExtractor.Extract(plan).HasImplicitConversion);
    }

    [Fact]
    public void Convert_implicit_owned_by_an_operator_is_a_conversion()
    {
        const string plan = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan><RelOp NodeId="0" PhysicalOp="Compute Scalar"><ComputeScalar><DefinedValues><DefinedValue><ScalarOperator ScalarString="CONVERT_IMPLICIT(int,[c],0)" /></DefinedValue></DefinedValues></ComputeScalar></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

        Assert.True(PlanMetricsExtractor.Extract(plan).HasImplicitConversion);
    }

    [Fact]
    public void Out_of_range_dop_is_ignored()
    {
        const string plan = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan DegreeOfParallelism="4294967297" /></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";
        const string explain = """[{ "Plan": { "Node Type": "Gather", "Workers Launched": 4294967297, "Shared Hit Blocks": 1 } }]""";

        Assert.Null(PlanMetricsExtractor.Extract(plan).Dop);
        Assert.Null(PlanMetricsExtractor.Extract(explain).Dop);
    }

    [Fact]
    public void Explain_without_buffers_has_null_postgres_counters()
    {
        const string explain = """[{ "Plan": { "Node Type": "Seq Scan", "Workers Launched": 1 } }]""";

        var metrics = PlanMetricsExtractor.Extract(explain);

        Assert.Null(metrics.Postgres);
        Assert.Equal(2, metrics.Dop);
    }
}