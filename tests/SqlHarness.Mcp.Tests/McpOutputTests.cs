using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;
using SqlHarness.TestFixtures;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T4 output contract: every adapted outcome validates against the shared
/// output schema — success, partial matrix, failures, unavailable CPU,
/// snapshot differences, and the watch deadline. Only failures set IsError;
/// the controlled exits 7/8 stay non-errors with an explicit status, and
/// protocol errors never reach the adapter (they stay with the SDK).
/// All data is synthetic; no database is opened.
/// </summary>
public sealed class McpOutputTests
{
    private static readonly SqlHarnessTargetIdentityReport Target = new("req-srv", "req-db", "srv", "db", "profile");

    private static JsonElement EnvelopeOf(CallToolResult result)
    {
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        Assert.Null(result.StructuredContent);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static void AssertValidEnvelope(JsonElement envelope) =>
        Assert.Empty(McpResultAdapter.ValidateEnvelope(envelope));

    private static void AssertV3Shape(JsonElement envelope)
    {
        AssertValidEnvelope(envelope);
        Assert.Equal(McpAgentEnvelope.Version, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.False(envelope.TryGetProperty("command", out _));
        Assert.False(envelope.TryGetProperty("exitCode", out _));
        Assert.False(envelope.TryGetProperty("operationRef", out _));
        Assert.False(envelope.TryGetProperty("parentOperationRef", out _));
        Assert.True(envelope.TryGetProperty("status", out _));
        Assert.True(envelope.TryGetProperty("result", out _));
    }

    private static void AssertOmitted(JsonElement envelope, string name) =>
        Assert.False(envelope.TryGetProperty(name, out _), $"optional field '{name}' must be omitted when empty");

    [Fact]
    public void Mcp_v3_projection_preserves_session_safety_report_warnings()
    {
        var report = new SqlHarnessQueryReport(Target, "read-only", [], [], 0, 1, "hash", new OutputFootprint(0, 0))
        {
            Warnings = ["isolation level SERIALIZABLE in effect"],
        };

        var envelope = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "sqlharness_query"));

        AssertV3Shape(envelope);
        Assert.Equal("isolation level SERIALIZABLE in effect",
            Assert.Single(envelope.GetProperty("result").GetProperty("warnings").EnumerateArray()).GetString());
    }

    [Fact]
    public void Mcp_v3_preserves_partial_and_controlled_outcomes_without_cli_fields()
    {
        var dryRun = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0), null),
            "sqlharness_query"));
        AssertV3Shape(dryRun);
        Assert.False(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0), null),
            "sqlharness_query").IsError == true);
        Assert.Equal("success", dryRun.GetProperty("status").GetString());
        Assert.Equal("read-only", dryRun.GetProperty("result").GetProperty("classification").GetString());
        Assert.Equal("read-only", dryRun.GetProperty("result").GetProperty("tier").GetString());
        AssertOmitted(dryRun, "error");
        AssertOmitted(dryRun, "truncation");
        Assert.Equal("passed", dryRun.GetProperty("result").GetProperty("validation").GetString());
        Assert.Equal("not-run", dryRun.GetProperty("result").GetProperty("execution").GetString());

        var validation = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "Unknown argument. Supported arguments: sql, file."),
            "sqlharness_query"));
        AssertV3Shape(validation);
        Assert.True(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "Unknown argument. Supported arguments: sql, file."),
            "sqlharness_query").IsError == true);
        Assert.Equal("error", validation.GetProperty("status").GetString());
        Assert.Equal("safety_rejected", validation.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("validation", validation.GetProperty("error").GetProperty("phase").GetString());

        var storage = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.LocalStorage, null, "storage denied",
                Error: new ArtifactStoragePreflightException("/tmp/benchmark-artifacts", new UnauthorizedAccessException("storage denied")).ToError([])),
            "sqlharness_compare"));
        AssertV3Shape(storage);
        Assert.Equal("local_storage_failed", storage.GetProperty("error").GetProperty("code").GetString());

        var auth = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Authentication, null, "Credentials were rejected."),
            "sqlharness_query"));
        AssertV3Shape(auth);
        Assert.Equal("authentication_failed", auth.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("authentication", auth.GetProperty("error").GetProperty("phase").GetString());

        var matrix = new SqlHarnessCompareMatrixReport("batch", "int", []);
        var partialResult = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, matrix, "Cell 2 failed."),
            "sqlharness_compare");
        var partial = EnvelopeOf(partialResult);
        AssertV3Shape(partial);
        Assert.True(partialResult.IsError == true);
        Assert.Equal("partial", partial.GetProperty("status").GetString());
        Assert.Equal("Cell 2 failed.", partial.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("batch", partial.GetProperty("result").GetProperty("parameterName").GetString());

        var watchResult = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.WatchMaxDuration,
                new SqlHarnessWatchReport(Target, 4, 900000, WatchExitReason.MaxDuration, []), null),
            "sqlharness_watch");
        var watch = EnvelopeOf(watchResult);
        AssertV3Shape(watch);
        Assert.False(watchResult.IsError == true);
        Assert.Equal("watch_max_duration", watch.GetProperty("status").GetString());
        Assert.Equal("watch_max_duration", watch.GetProperty("error").GetProperty("code").GetString());

        var snapshotResult = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.SnapshotDifferences,
                new SqlHarnessSnapshotReport(Target, "before-import", SnapshotVerdict.Different, 2,
                    [new SqlHarnessSnapshotDifference(0, 1, 2, "changed")]), null),
            "sqlharness_snapshot");
        var snapshot = EnvelopeOf(snapshotResult);
        AssertV3Shape(snapshot);
        Assert.False(snapshotResult.IsError == true);
        Assert.Equal("snapshot_differences", snapshot.GetProperty("status").GetString());
        Assert.Equal("snapshot_differences", snapshot.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, snapshot.GetProperty("result").GetProperty("differenceCount").GetInt32());
    }

    [Fact]
    public void Mcp_v3_uses_named_enums_and_omits_empty_optional_fields()
    {
        var compare = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m") with
            {
                Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Multiset, true, null, 0, 0),
            }, null),
            "sqlharness_compare"));
        AssertV3Shape(compare);
        Assert.Equal("multiset", compare.GetProperty("result").GetProperty("equivalence").GetProperty("mode").GetString());
        AssertOmitted(compare, "error");
        AssertOmitted(compare, "truncation");

        var ordered = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m") with
            {
                Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Ordered, true, null, 0, 0),
            }, null),
            "sqlharness_compare"));
        Assert.Equal("ordered", ordered.GetProperty("result").GetProperty("equivalence").GetProperty("mode").GetString());

        var set = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m") with
            {
                Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Set, true, null, 0, 0),
            }, null),
            "sqlharness_compare"));
        Assert.Equal("set", set.GetProperty("result").GetProperty("equivalence").GetProperty("mode").GetString());

        var off = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m") with
            {
                Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null),
            }, null),
            "sqlharness_compare"));
        Assert.Equal("off", off.GetProperty("result").GetProperty("equivalence").GetProperty("mode").GetString());

        var watch = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success,
                new SqlHarnessWatchReport(Target, 1, 10, WatchExitReason.ConditionMet, []), null),
            "sqlharness_watch"));
        Assert.Equal("condition-met", watch.GetProperty("result").GetProperty("exitReason").GetString());
        var unchanged = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success,
                new SqlHarnessWatchReport(Target, 1, 10, WatchExitReason.Unchanged, []), null),
            "sqlharness_watch"));
        Assert.Equal("unchanged", unchanged.GetProperty("result").GetProperty("exitReason").GetString());
        var maxDuration = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.WatchMaxDuration,
                new SqlHarnessWatchReport(Target, 1, 10, WatchExitReason.MaxDuration, []), null),
            "sqlharness_watch"));
        Assert.Equal("max-duration", maxDuration.GetProperty("result").GetProperty("exitReason").GetString());

        var stored = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success,
                new SqlHarnessSnapshotReport(Target, "baseline", SnapshotVerdict.Stored, 0, []), null),
            "sqlharness_snapshot"));
        Assert.Equal("stored", stored.GetProperty("result").GetProperty("verdict").GetString());
        var identical = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success,
                new SqlHarnessSnapshotReport(Target, "baseline", SnapshotVerdict.Identical, 0, []), null),
            "sqlharness_snapshot"));
        Assert.Equal("identical", identical.GetProperty("result").GetProperty("verdict").GetString());
        var different = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.SnapshotDifferences,
                new SqlHarnessSnapshotReport(Target, "baseline", SnapshotVerdict.Different, 1,
                    [new SqlHarnessSnapshotDifference(0, 1, 2, "changed")]), null),
            "sqlharness_snapshot"));
        Assert.Equal("different", different.GetProperty("result").GetProperty("verdict").GetString());

        var validation = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "Rejected."),
            "sqlharness_query"));
        Assert.False(validation.GetProperty("error").TryGetProperty("hint", out _));
        Assert.False(validation.GetProperty("error").TryGetProperty("location", out _));
    }

    [Fact]
    public async Task Offline_internal_failure_does_not_claim_sql_was_executed()
    {
        Assert.NotEqual(
            SqlHarnessExitCode.SqlExecution,
            OperationFailureMapper.Map(new InvalidOperationException("offline boom"), OperationPhase.Validation));

        var profile = new TargetProfile("offline-host.invalid", "offlinedb", new Dictionary<string, string>(), "integrated");
        var scope = McpScope.Create(new McpServerOptions { Profile = "offline-profile" },
            new Dictionary<string, TargetProfile> { ["offline-profile"] = profile });
        var handlers = new McpToolHandlers(scope, new ThrowingOfflineModule());

        var result = await handlers.PlanAsync(null!, content: "<ShowPlanXML/>");
        var envelope = EnvelopeOf(result);
        AssertV3Shape(envelope);
        Assert.True(result.IsError == true);
        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal(McpAgentError.InternalCode, envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(McpAgentError.InternalPhase, envelope.GetProperty("error").GetProperty("phase").GetString());
        Assert.DoesNotContain("sql_execution_failed", envelope.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"phase\":\"sql\"", envelope.GetRawText(), StringComparison.Ordinal);
    }

    private sealed class ThrowingOfflineModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            throw new InvalidOperationException("synthetic offline failure");
    }

    [Fact]
    public void Emitted_footprint_counts_text_bytes_and_lines_only()
    {
        const string text = "first\né";
        using var structured = JsonDocument.Parse("{\"hidden\":\"structured\"}");
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = structured.RootElement.Clone(),
        };

        Assert.Equal(new OutputFootprint(Encoding.UTF8.GetByteCount(text), 2), McpResultAdapter.EmittedFootprint(result));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public void Storage_preflight_error_preserves_diagnostics_in_the_wire_budget(int maximumBytes)
    {
        var exception = new ArtifactStoragePreflightException("/tmp/benchmark-artifacts",
            new UnauthorizedAccessException("storage denied"));
        var error = exception.ToError([]);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.LocalStorage, null, error.Message, Error: error);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_compare", new McpResultBudget(maximumBytes));

        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.True(result.IsError);
        var emitted = envelope.GetProperty("error");
        Assert.Equal("local_storage_failed", emitted.GetProperty("code").GetString());
        Assert.Equal("artifact-preflight", emitted.GetProperty("phase").GetString());
        Assert.Contains("process account", emitted.GetProperty("hint").GetString());
        Assert.Equal("/tmp/benchmark-artifacts", emitted.GetProperty("location").GetProperty("path").GetString());
    }

    [Fact]
    public void Success_carries_a_valid_envelope_without_error()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0),
            null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        Assert.False(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertV3Shape(envelope);
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        AssertOmitted(envelope, "error");
        Assert.Equal("read-only", envelope.GetProperty("result").GetProperty("classification").GetString());
    }

    [Fact]
    public void Compare_result_carries_the_nullability_diagnostic()
    {
        var report = BuildCompareReport("m") with
        {
            Equivalence = new ResultEquivalenceReport(
                ResultComparisonMode.Ordered, false, 0, 0, 0, true, false, ResultMismatchReasons.Nullability,
                true, true, true, [ResultMismatchReasons.Nullability]),
        };
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var envelope = EnvelopeOf(McpResultAdapter.Adapt(outcome, "sqlharness_compare"));

        var equivalence = envelope.GetProperty("result").GetProperty("equivalence");
        Assert.True(equivalence.GetProperty("compareNullability").GetBoolean());
        Assert.False(equivalence.GetProperty("nullabilityEquivalent").GetBoolean());
        Assert.Equal("nullability", equivalence.GetProperty("mismatchReason").GetString());
        Assert.Equal(["nullability"], equivalence.GetProperty("mismatchReasons").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.True(equivalence.GetProperty("baselineStable").GetBoolean());
        Assert.True(equivalence.GetProperty("schemaEquivalent").GetBoolean());
    }

    [Fact]
    public void Off_compare_omits_unknown_equivalence_diagnostics()
    {
        var report = BuildCompareReport("m") with
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null),
        };

        var equivalence = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "sqlharness_compare"))
            .GetProperty("result").GetProperty("equivalence");

        Assert.Equal("off", equivalence.GetProperty("mode").GetString());
        Assert.False(equivalence.TryGetProperty("baselineStable", out _));
        Assert.False(equivalence.TryGetProperty("candidateStable", out _));
        Assert.False(equivalence.TryGetProperty("schemaEquivalent", out _));
        Assert.False(equivalence.TryGetProperty("mismatchReasons", out _));
    }

    [Fact]
    public void Ordered_row_mismatch_keeps_ordered_mode_and_does_not_emit_order_only()
    {
        var report = BuildCompareReport("m") with
        {
            Equivalence = new ResultEquivalenceReport(
                ResultComparisonMode.Ordered, false, 2, 0, 0,
                BaselineStable: true, CandidateStable: true, SchemaEquivalent: true,
                MismatchReasons: [ResultMismatchReasons.Rows]),
        };

        var result = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "sqlharness_compare");
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        var equivalence = EnvelopeOf(result).GetProperty("result").GetProperty("equivalence");

        Assert.Equal("ordered", equivalence.GetProperty("mode").GetString());
        Assert.Equal(["rows"], equivalence.GetProperty("mismatchReasons").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.DoesNotContain("order-only", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_matrix_batch_is_partial_but_still_an_error()
    {
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int", []);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, matrix, "Cell 2 failed.");

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_compare");

        Assert.True(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal("partial", envelope.GetProperty("status").GetString());
        Assert.Equal("Cell 2 failed.", envelope.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("batch", envelope.GetProperty("result").GetProperty("parameterName").GetString());
        Assert.False(envelope.GetProperty("error").TryGetProperty("matrixStop", out _));
        Assert.False(envelope.GetProperty("result").TryGetProperty("coverage", out _));
    }

    [Fact]
    public void First_cell_matrix_failure_puts_coverage_on_error_matrix_stop_only()
    {
        var coverage = MatrixExecutionCoverage.IdentifiedFailure(
            32, 0, new MatrixFailedCase(0, ParameterValue: "1", Phase: "sql") { IncludeParameterValue = true });
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.SqlExecution, null, "Comparison matrix cell 0 failed.",
            Error: SqlHarnessError.From(SqlHarnessExitCode.SqlExecution, "Comparison matrix cell 0 failed.") with { MatrixStop = coverage })
        {
            Coverage = coverage,
        };

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_compare");
        var envelope = EnvelopeOf(result);

        Assert.True(result.IsError == true);
        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("result").ValueKind);
        var stop = envelope.GetProperty("error").GetProperty("matrixStop");
        Assert.Equal(32, stop.GetProperty("requestedCases").GetInt32());
        Assert.Equal(0, stop.GetProperty("completedCases").GetInt32());
        Assert.Equal(31, stop.GetProperty("notExecutedCases").GetInt32());
        Assert.Equal(0, stop.GetProperty("failedCase").GetProperty("index").GetInt32());
        Assert.Equal("1", stop.GetProperty("failedCase").GetProperty("parameterValue").GetString());
        Assert.Equal("sql", stop.GetProperty("failedCase").GetProperty("phase").GetString());
        Assert.False(envelope.GetProperty("result").ValueKind == JsonValueKind.Object &&
            envelope.GetProperty("result").TryGetProperty("coverage", out _));
    }

    [Fact]
    public void Partial_matrix_puts_coverage_on_result_never_error()
    {
        var compare = BuildCompareReport("ok");
        var cells = Enumerable.Range(0, 15).Select(i => new CompareMatrixCellReport(i, (i + 1).ToString(), compare)).ToArray();
        var coverage = MatrixExecutionCoverage.IdentifiedFailure(
            32, 15, new MatrixFailedCase(15, ParameterValue: "16", Phase: "sql") { IncludeParameterValue = true });
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int", cells) { Coverage = coverage };
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, matrix, "Comparison matrix cell 15 failed.")
        {
            Coverage = coverage,
        };

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_compare", new McpResultBudget(65536, 128));
        var envelope = EnvelopeOf(result);
        var resultObj = envelope.GetProperty("result");
        var reported = resultObj.GetProperty("coverage");
        Assert.Equal("partial", envelope.GetProperty("status").GetString());
        Assert.Equal(32, reported.GetProperty("requestedCases").GetInt32());
        Assert.Equal(15, reported.GetProperty("completedCases").GetInt32());
        Assert.Equal(16, reported.GetProperty("notExecutedCases").GetInt32());
        Assert.Equal(15, reported.GetProperty("failedCase").GetProperty("index").GetInt32());
        Assert.False(envelope.GetProperty("error").TryGetProperty("matrixStop", out _));
        Assert.False(resultObj.TryGetProperty("details", out var details) && details.GetArrayLength() > 0);
    }

    [Fact]
    public void Partial_measure_keeps_coverage_on_error_matrix_stop()
    {
        var coverage = MatrixExecutionCoverage.IdentifiedFailure(
            2, 1, new MatrixFailedCase(1, SetName: "large", Phase: "sql"));
        var report = new SqlHarnessMeasureSetReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            2,
            2,
            1,
            ["small"],
            "Measured runs execute parameter sets sequentially in user-supplied order.",
            "Parameter-set measurements use separate sessions; SQLHarness did not clear or isolate the server plan cache.",
            [new MeasureParameterSetReport(
                "small",
                [new MeasureParameterMetadata("@id", "int")],
                "value-hash",
                2,
                true,
                "result-hash",
                new CompareVariantReport("small", new(1, 1, 1), new(2, 2, 2), new(3, 3, 3), new Dictionary<string, long>(), [], []),
                [])],
            new MeasureCrossSetSummary(null, 0, null, 0, null, 0, null, 0, null, 0, null, 0),
            null);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, report, "Measure parameter set 1 failed.",
            Error: SqlHarnessError.From(SqlHarnessExitCode.SqlExecution, "Measure parameter set 1 failed.") with { MatrixStop = coverage })
        {
            Coverage = coverage,
        };

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_measure");
        var envelope = EnvelopeOf(result);

        Assert.True(result.IsError == true);
        Assert.Equal("partial", envelope.GetProperty("status").GetString());
        var stop = envelope.GetProperty("error").GetProperty("matrixStop");
        Assert.Equal(2, stop.GetProperty("requestedCases").GetInt32());
        Assert.Equal(1, stop.GetProperty("completedCases").GetInt32());
        Assert.Equal(0, stop.GetProperty("notExecutedCases").GetInt32());
        Assert.Equal(1, stop.GetProperty("failedCase").GetProperty("index").GetInt32());
        Assert.Equal("large", stop.GetProperty("failedCase").GetProperty("setName").GetString());
        Assert.Equal("sql", stop.GetProperty("failedCase").GetProperty("phase").GetString());
    }

    [Fact]
    public void Failure_carries_a_stable_error_object_and_is_error()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "The query was cancelled.");

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        Assert.True(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal("sql_execution_failed", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("result").ValueKind);
    }

    [Theory]
    [InlineData(SqlHarnessExitCode.Safety, "safety_rejected")]
    [InlineData(SqlHarnessExitCode.Authentication, "authentication_failed")]
    [InlineData(SqlHarnessExitCode.TargetMismatch, "target_mismatch")]
    [InlineData(SqlHarnessExitCode.SqlExecution, "sql_execution_failed")]
    [InlineData(SqlHarnessExitCode.LocalStorage, "local_storage_failed")]
    public void Failure_exit_codes_keep_numeric_exit_and_stable_cause_code(SqlHarnessExitCode exitCode, string expectedCode)
    {
        var outcome = new SqlHarnessOutcome(exitCode, null, "Safe failure message.");

        var envelope = EnvelopeOf(McpResultAdapter.Adapt(outcome, "sqlharness_query"));

        AssertValidEnvelope(envelope);
        Assert.Equal(expectedCode, envelope.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Unavailable_cpu_stays_a_success_with_explicit_warning()
    {
        var query = new BenchmarkVariantSummary(
            new CompareDistribution(0, 0, 0),
            new CompareDistribution(10, 12, 15),
            new CompareDistribution(100, 110, 120),
            new Dictionary<string, CompareDistribution>(),
            [BenchmarkMetricText.PostgresCpuUnavailable])
        {
            MetricReport = new BenchmarkMetricReport(
                BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Measured, null, true, null, null,
                BenchmarkMetricReport.Measured, null, null, null,
                BenchmarkMetricReport.ResultStatement, BenchmarkMetricText.StatementRows,
                [BenchmarkMetricText.PostgresCpuUnavailable]),
        };
        var summary = new MeasureBenchmarkSummary(
            Target, 5, true,
            new BenchmarkClassificationReport("none", "read-only"), [],
            query, [], null);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, summary, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_measure");

        Assert.False(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        var projected = envelope.GetProperty("result").GetProperty("query");
        Assert.Equal(JsonValueKind.Null, projected.GetProperty("cpuTimeMilliseconds").ValueKind);
        Assert.Equal("unavailable", projected.GetProperty("cpuTimeAvailability").GetString());
        Assert.Contains(
            BenchmarkMetricText.PostgresCpuUnavailable,
            projected.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Mcp_compare_emits_artifact_id_resolved_target_and_truthful_metrics()
    {
        var owner = new ArtifactOwner("profile", new Dictionary<string, string>(), "sqlserver", "sql-a", "AdventureWorks");
        var home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-id-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(home, "deep", new string('p', 90), "artifacts");
        Directory.CreateDirectory(parent);
        try
        {
            var id = "2026.10.16T000000000Z-host.example-ab12cd34ef";
            var directory = Path.Combine(parent, id);
            Directory.CreateDirectory(directory);
            var zeroCpu = new CompareDistribution(0, 0, 0);
            var exactElapsed = new FractionalMilliseconds(0.3m, 0.4m, 0.5m);
            var baseline = new CompareVariantReport(
                "baseline",
                zeroCpu,
                new CompareDistribution(0, 0, 1),
                new CompareDistribution(0, 0, 0),
                new Dictionary<string, long>(),
                [],
                [])
            {
                MetricReport = new BenchmarkMetricReport(
                    BenchmarkMetricReport.Measured, BenchmarkMetricReport.Measured, exactElapsed, false,
                    null, exactElapsed, BenchmarkMetricReport.Measured, null, null, null,
                    BenchmarkMetricReport.ResultStatement, BenchmarkMetricText.StatementRows, []),
            };
            var candidate = baseline with
            {
                Name = "candidate",
                CpuTimeMilliseconds = new CompareDistribution(0, 0, 0),
                LogicalReads = new CompareDistribution(0, 0, 0),
                MetricReport = new BenchmarkMetricReport(
                    BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Measured,
                    new FractionalMilliseconds(0.1m, 0.2m, 0.3m), false, null, null,
                    BenchmarkMetricReport.Unavailable, BenchmarkMetricText.PostgresLogicalReadsSource,
                    BenchmarkMetricText.PostgresRelationBufferSource, false,
                    BenchmarkMetricReport.ResultStatement, BenchmarkMetricText.StatementRows,
                    [BenchmarkMetricText.PostgresCpuUnavailable]),
            };
            var report = new SqlHarnessCompareReport(
                new SqlHarnessTargetIdentityReport("requested-sql", "AdventureWorks", "sql-a", "AdventureWorks", "profile",
                    Engine: "sqlserver", TransportPolicy: "strict"),
                5, 10, true, baseline, candidate, directory)
            {
                Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Ordered, true, 0, 0, 0),
                Classification = new CompareClassificationReport("none", "read-only", "read-only"),
                Regression = new RegressionReport(
                    "pass", [], 2,
                    new RegressionDiagnostic("not-regressed", 0.4m, 0.2m, -0.2m, -50m),
                    new RegressionDiagnostic("unavailable", null, null, null, null),
                    new RegressionDiagnostic("unavailable", null, null, null, null)),
            };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(
                ArtifactManifest.ForReport(report, owner), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(
                report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var result = McpResultAdapter.Adapt(
                new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
                "sqlharness_compare",
                new McpResultBudget(65536, 8));
            var envelope = EnvelopeOf(result);
            AssertV3Shape(envelope);
            var projected = envelope.GetProperty("result");
            Assert.Equal(id, projected.GetProperty("artifactId").GetString());
            Assert.False(projected.TryGetProperty("artifactDirectory", out _));
            Assert.DoesNotContain(parent, envelope.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("sql-a", projected.GetProperty("target").GetProperty("server").GetString());
            Assert.Equal("AdventureWorks", projected.GetProperty("target").GetProperty("database").GetString());
            Assert.Equal("sqlserver", projected.GetProperty("target").GetProperty("engine").GetString());
            Assert.Equal("requested-sql", projected.GetProperty("target").GetProperty("requestedServer").GetString());
            Assert.False(projected.GetProperty("target").TryGetProperty("requestedDatabase", out _));
            Assert.Equal("strict", projected.GetProperty("target").GetProperty("transportPolicy").GetString());
            Assert.False(projected.GetProperty("target").TryGetProperty("mode", out _));
            Assert.False(projected.GetProperty("target").TryGetProperty("actualServer", out _));

            var baselineMetrics = projected.GetProperty("baseline");
            Assert.Equal(0, baselineMetrics.GetProperty("cpuTimeMilliseconds").GetProperty("median").GetDecimal());
            Assert.Equal(0.4m, baselineMetrics.GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetDecimal());
            Assert.Equal(0.3m, baselineMetrics.GetProperty("elapsedTimeMilliseconds").GetProperty("min").GetDecimal());
            Assert.Equal(0, baselineMetrics.GetProperty("logicalReads").GetProperty("median").GetDecimal());
            Assert.False(baselineMetrics.TryGetProperty("elapsedTimeMillisecondsExact", out _));
            Assert.False(baselineMetrics.TryGetProperty("metricReport", out _));
            Assert.Equal(JsonValueKind.Null, projected.GetProperty("candidate").GetProperty("cpuTimeMilliseconds").ValueKind);
            Assert.Equal(JsonValueKind.Null, projected.GetProperty("candidate").GetProperty("logicalReads").ValueKind);
            Assert.Equal("unavailable", projected.GetProperty("candidate").GetProperty("cpuTimeAvailability").GetString());
            Assert.False(projected.GetProperty("candidate").GetProperty("relationBuffersAreAdditive").GetBoolean());
            var regression = projected.GetProperty("regression");
            Assert.Equal("pass", regression.GetProperty("verdict").GetString());
            Assert.Equal(-0.2m, regression.GetProperty("elapsed").GetProperty("delta").GetDecimal());
            Assert.False(regression.GetProperty("elapsed").TryGetProperty("baselineMedian", out _));
            Assert.False(regression.GetProperty("elapsed").TryGetProperty("candidateMedian", out _));

            var section = Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(parent, id, "metrics", owner));
            Assert.Equal(id, section.ArtifactId);
            Assert.Throws<ArtifactReadException>(() =>
                ArtifactReader.ReadSection(parent, id, "metrics", owner with { Server = "sql-b" }));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Mcp_omits_unusable_artifact_id_and_does_not_clip_a_valid_id()
    {
        var missing = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m"), null),
            "sqlharness_compare"));
        Assert.False(missing.GetProperty("result").TryGetProperty("artifactId", out _));
        Assert.False(missing.GetProperty("result").TryGetProperty("artifactDirectory", out _));

        var invalid = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m", @"C:\artifacts\-leading-dash"), null),
            "sqlharness_compare"));
        Assert.False(invalid.GetProperty("result").TryGetProperty("artifactId", out _));

        var overlongLeaf = new string('a', 129);
        var overlong = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m", Path.Combine(@"C:\artifacts", overlongLeaf)), null),
            "sqlharness_compare",
            new McpResultBudget(65536, 8)));
        Assert.False(overlong.GetProperty("result").TryGetProperty("artifactId", out _));
        Assert.DoesNotContain(overlongLeaf[..8], overlong.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mcp_resolved_target_distinguishes_same_database_on_different_servers()
    {
        var left = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m") with
            {
                Target = new SqlHarnessTargetIdentityReport("sql-a", "AdventureWorks", "sql-a", "AdventureWorks", "profile", Engine: "sqlserver"),
            }, null),
            "sqlharness_compare"));
        var right = EnvelopeOf(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompareReport("m") with
            {
                Target = new SqlHarnessTargetIdentityReport("sql-b", "AdventureWorks", "sql-b", "AdventureWorks", "profile", Engine: "sqlserver"),
            }, null),
            "sqlharness_compare"));

        Assert.Equal("AdventureWorks", left.GetProperty("result").GetProperty("target").GetProperty("database").GetString());
        Assert.Equal("AdventureWorks", right.GetProperty("result").GetProperty("target").GetProperty("database").GetString());
        Assert.Equal("sql-a", left.GetProperty("result").GetProperty("target").GetProperty("server").GetString());
        Assert.Equal("sql-b", right.GetProperty("result").GetProperty("target").GetProperty("server").GetString());
        Assert.False(left.GetProperty("result").GetProperty("target").TryGetProperty("requestedServer", out _));
    }

    [Fact]
    public void Mcp_measure_set_keeps_repetitions_elapsed_range_and_cross_set_extrema()
    {
        var sets = Enumerable.Range(0, 20).Select(i =>
        {
            var metrics = new CompareVariantReport(
                $"set-{i}",
                new CompareDistribution(i, i + 1, i + 2),
                new CompareDistribution(10 * (i + 1), 20 * (i + 1), 40 * (i + 1)),
                new CompareDistribution(1, 2, 3),
                new Dictionary<string, long>(),
                [],
                i == 0 ? ["PlanWarning"] : [])
            {
                MetricReport = i == 0
                    ? new BenchmarkMetricReport(
                        BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Measured, null, true, null, null,
                        BenchmarkMetricReport.Unavailable, BenchmarkMetricText.PostgresLogicalReadsSource,
                        BenchmarkMetricText.PostgresRelationBufferSource, false,
                        BenchmarkMetricReport.ResultStatement, BenchmarkMetricText.StatementRows,
                        [BenchmarkMetricText.PostgresCpuUnavailable])
                    : null,
            };
            return new MeasureParameterSetReport(
                $"set-{i}-" + new string('n', 80),
                [new MeasureParameterMetadata("@id", "int")],
                $"hash-{i}",
                i + 2,
                true,
                null,
                metrics,
                ["plan-hash-should-stay-out"]);
        }).ToArray();
        var minSet = "set-0-" + new string('n', 80);
        var maxSet = "set-19-" + new string('n', 80);
        var report = new SqlHarnessMeasureSetReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile", Engine: "postgres"),
            5, 20, 1, sets.Select(set => set.Name).ToArray(), "order-rule", "cache-warning",
            sets,
            new MeasureCrossSetSummary(minSet, 0, maxSet, 1, null, 0, null, 0, null, 0, null, 0)
            {
                CpuTimeAvailability = BenchmarkMetricReport.Unavailable,
                ElapsedTimeAvailability = BenchmarkMetricReport.Measured,
                LogicalReadsAvailability = BenchmarkMetricReport.Unavailable,
                ElapsedWholeMillisecondsAreExact = false,
                MinimumMedianElapsedMillisecondsExact = 0.3m,
                MaximumMedianElapsedMillisecondsExact = 0.8m,
            },
            @"C:\artifacts\sets\20260101T000000000Z-sets-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var result = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
            "sqlharness_measure",
            new McpResultBudget(4096, 32));
        var envelope = EnvelopeOf(result);
        AssertV3Shape(envelope);
        var projected = envelope.GetProperty("result");
        Assert.Equal("20260101T000000000Z-sets-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", projected.GetProperty("artifactId").GetString());
        Assert.False(projected.TryGetProperty("artifactDirectory", out _));
        Assert.Equal(minSet, projected.GetProperty("crossSetSummary").GetProperty("minimumMedianElapsedSet").GetString());
        Assert.Equal(maxSet, projected.GetProperty("crossSetSummary").GetProperty("maximumMedianElapsedSet").GetString());
        var cross = projected.GetProperty("crossSetSummary");
        Assert.Equal(0.3m, cross.GetProperty("minimumMedianElapsedMilliseconds").GetDecimal());
        Assert.Equal(0.8m, cross.GetProperty("maximumMedianElapsedMilliseconds").GetDecimal());
        Assert.False(cross.TryGetProperty("minimumMedianElapsedMillisecondsExact", out _));
        Assert.False(cross.TryGetProperty("maximumMedianElapsedMillisecondsExact", out _));
        Assert.False(cross.TryGetProperty("elapsedWholeMillisecondsAreExact", out _));
        Assert.Equal("unavailable", cross.GetProperty("cpuTimeAvailability").GetString());
        Assert.Equal("unavailable", cross.GetProperty("logicalReadsAvailability").GetString());
        Assert.Equal(JsonValueKind.Null, cross.GetProperty("minimumMedianCpuMilliseconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, cross.GetProperty("maximumMedianCpuMilliseconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, cross.GetProperty("minimumMedianLogicalReads").ValueKind);
        Assert.Equal(JsonValueKind.Null, cross.GetProperty("maximumMedianLogicalReads").ValueKind);
        Assert.Equal(JsonValueKind.Null, cross.GetProperty("minimumMedianCpuSet").ValueKind);
        Assert.Equal(JsonValueKind.Null, cross.GetProperty("minimumMedianReadsSet").ValueKind);
        Assert.True(projected.GetProperty("sets").GetArrayLength() < 20, envelope.GetRawText());
        var first = projected.GetProperty("sets")[0];
        Assert.Equal(2, first.GetProperty("repetitions").GetInt32());
        Assert.Equal(10, first.GetProperty("elapsedTimeMilliseconds").GetProperty("min").GetInt64());
        Assert.Equal(20, first.GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetInt64());
        Assert.Equal(40, first.GetProperty("elapsedTimeMilliseconds").GetProperty("max").GetInt64());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("cpuTimeMilliseconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("logicalReads").ValueKind);
        Assert.Equal("unavailable", first.GetProperty("cpuTimeAvailability").GetString());
        Assert.Equal("unavailable", first.GetProperty("logicalReadsAvailability").GetString());
        Assert.Equal("measured", first.GetProperty("elapsedTimeAvailability").GetString());
        Assert.Equal(BenchmarkMetricText.PostgresLogicalReadsSource, first.GetProperty("logicalReadsSource").GetString());
        Assert.Equal(BenchmarkMetricText.PostgresRelationBufferSource, first.GetProperty("relationBufferSource").GetString());
        Assert.False(first.GetProperty("relationBuffersAreAdditive").GetBoolean());
        Assert.Equal(BenchmarkMetricReport.ResultStatement, first.GetProperty("resultRowSource").GetString());
        Assert.False(first.TryGetProperty("resultRowSourceDescription", out _));
        Assert.False(first.TryGetProperty("metricReport", out _));
        var warnings = first.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains("PlanWarning", warnings);
        Assert.Contains(BenchmarkMetricText.PostgresCpuUnavailable, warnings);
        Assert.False(first.TryGetProperty("valueHash", out _));
        Assert.DoesNotContain("plan-hash-should-stay-out", envelope.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("hash-0", envelope.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_differences_are_a_controlled_outcome_not_an_error()
    {
        var report = new SqlHarnessSnapshotReport(
            Target, "before-import", SnapshotVerdict.Different, 2,
            [new SqlHarnessSnapshotDifference(0, 1, 2, "changed"),
             new SqlHarnessSnapshotDifference(0, 3, null, "removed")]);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SnapshotDifferences, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_snapshot");

        Assert.False(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal("snapshot_differences", envelope.GetProperty("status").GetString());
        Assert.Equal("snapshot_differences", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, envelope.GetProperty("result").GetProperty("differenceCount").GetInt32());
    }

    [Fact]
    public void Watch_deadline_is_a_controlled_outcome_not_an_error()
    {
        var report = new SqlHarnessWatchReport(Target, 4, 900000, WatchExitReason.MaxDuration, []);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.WatchMaxDuration, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_watch");

        Assert.False(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal("watch_max_duration", envelope.GetProperty("status").GetString());
        Assert.Equal("watch_max_duration", envelope.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Minimum_error_envelope_fits_the_smallest_budget()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "Rejected.");

        var result = McpResultAdapter.Adapt(
            outcome, "sqlharness_query", new McpResultBudget(McpLimits.MinCallToolResultBudgetBytes));

        Assert.True(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.True(
            McpResultAdapter.MeasureBytes(result) <= McpLimits.MinCallToolResultBudgetBytes,
            "The minimum valid error envelope must fit the smallest budget.");
    }

    [Fact]
    public void Validation_failure_keeps_stable_code_and_never_truncates_json()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "Unknown argument. Supported arguments: sql, file.");

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        Assert.True(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal("safety_rejected", envelope.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Small_query_projection_fits_budget_without_truncation()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var columns = new[]
        {
            new SqlHarnessColumnReport(0, "a", "int", true),
            new SqlHarnessColumnReport(1, "b", "int", true),
            new SqlHarnessColumnReport(2, "c", "int", true),
        };
        var rows = Enumerable.Range(1, 3)
            .Select(i => new object?[] { i, i * 10, i * 100 })
            .ToArray();
        var set = new SqlHarnessResultSetReport(columns, rows, rows.Length, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(2151, 1));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        Assert.False(result.IsError == true);
        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.DefaultCallToolResultBudgetBytes,
            "Small query result must fit the default text budget.");
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        AssertOmitted(envelope, "truncation");
        var resultSets = envelope.GetProperty("result").GetProperty("resultSets");
        Assert.Equal(1, resultSets.GetArrayLength());
        Assert.Equal(3, resultSets[0].GetProperty("rows").GetArrayLength());
    }

    [Theory]
    [InlineData(null, 2048)]
    [InlineData(0, 0)]
    [InlineData(2048, 2048)]
    [InlineData(4096, 4096)]
    public void Resolved_cell_limit_is_applied_and_reported(int? requested, int expected)
    {
        var budget = McpResultBudget.Resolve(65536, null, requested, AgentOutputLimits.MaximumCellCharacters);
        var text = new string('x', 5000);
        var set = new SqlHarnessResultSetReport(
            [new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(Target, "read-only", [set], [], 0, 1, "hash", new OutputFootprint(5000, 1));

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
            "sqlharness_query", budget);

        var envelope = EnvelopeOf(result);
        Assert.Equal(expected, envelope.GetProperty("truncation").GetProperty("maxCellChars").GetInt32());
        Assert.Equal(expected, envelope.GetProperty("result").GetProperty("resultSets")[0]
            .GetProperty("rows")[0][0].GetString()!.Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Resolved_cell_limit_rejects_values_outside_contract(int requested)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            McpResultBudget.Resolve(65536, null, requested, AgentOutputLimits.MaximumCellCharacters));

        Assert.Contains("Cell limit must be 0..4096 characters.", exception.Message);
    }

    [Fact]
    public void Large_query_projection_truncates_within_explicit_text_budget()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var columns = new[]
        {
            new SqlHarnessColumnReport(0, "a", "int", true),
            new SqlHarnessColumnReport(1, "b", "int", true),
            new SqlHarnessColumnReport(2, "c", "int", true),
        };
        var rows = Enumerable.Range(1, 1000)
            .Select(i => new object?[] { i, i * 10, i * 100 })
            .ToArray();
        var set = new SqlHarnessResultSetReport(columns, rows, rows.Length, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(50000, 1));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        const int budget = 16 * 1024;
        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(budget));

        Assert.False(result.IsError == true);
        Assert.True(McpResultAdapter.MeasureBytes(result) <= budget,
            "Large query projection must still fit the explicit result-text budget.");
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("truncation").ValueKind);
        Assert.True(envelope.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Thirty_two_mixed_cases_at_4096_preserve_decisions_across_pages()
    {
        var owner = new ArtifactOwner("profile", new Dictionary<string, string>(), "sqlserver", "server", "db");
        var root = Path.Combine(Path.GetTempPath(), $"sqlharness-mcp-matrix32-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var cells = Enumerable.Range(0, 32).Select(i => MixedMatrixCell(root, owner, i)).ToArray();
            var coverage = MatrixExecutionCoverage.AllCompleted(32);
            var matrix = new SqlHarnessCompareMatrixReport("batch", "int", cells) { Coverage = coverage };
            new CompareArtifactWriter(root, () => DateTimeOffset.UnixEpoch).WriteMatrixIndex(cells, coverage);
            var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null) { Coverage = coverage };

            var recovered = new List<(int Index, string? ArtifactId, string? Verdict, string? ParameterValue)>();
            var result = McpResultAdapter.Adapt(outcome, "sqlharness_compare", new McpResultBudget(4096, 128));
            Assert.True(McpResultAdapter.MeasureBytes(result) <= 4096, Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
            var envelope = EnvelopeOf(result);
            AssertValidEnvelope(envelope);
            var projected = envelope.GetProperty("result");
            Assert.False(projected.TryGetProperty("details", out var details) && details.GetArrayLength() > 0);
            var pageCoverage = projected.GetProperty("coverage");
            Assert.Equal(32, pageCoverage.GetProperty("requestedCases").GetInt32());
            Assert.Equal(32, pageCoverage.GetProperty("completedCases").GetInt32());
            Assert.Equal(0, pageCoverage.GetProperty("notExecutedCases").GetInt32());
            Assert.False(pageCoverage.TryGetProperty("failedCase", out _));
            Assert.False(envelope.TryGetProperty("error", out var error) && error.TryGetProperty("matrixStop", out _));
            CollectDecisionCases(projected.GetProperty("cases"), recovered);
            Assert.True(projected.GetProperty("cases").GetArrayLength() < 32);
            Assert.True(projected.GetProperty("omittedCaseCount").GetInt32() > 0);
            var matrixArtifactId = projected.GetProperty("matrixArtifactId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(matrixArtifactId));
            var cursor = projected.TryGetProperty("continuation", out var continuation) && continuation.ValueKind == JsonValueKind.Number
                ? continuation.GetInt32()
                : (int?)null;
            var previousCursor = -1;
            while (cursor is { } pageCursor)
            {
                Assert.True(pageCursor > previousCursor);
                previousCursor = pageCursor;
                var page = Assert.IsType<ArtifactMatrixCellsSection>(ArtifactReader.ReadSection(
                    root, matrixArtifactId!, "matrix-cells", owner, pageCursor));
                var pageOutcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, page, null);
                var pageResult = McpResultAdapter.Adapt(pageOutcome, "sqlharness_artifact", new McpResultBudget(4096, 128));
                Assert.True(McpResultAdapter.MeasureBytes(pageResult) <= 4096);
                var pageEnvelope = EnvelopeOf(pageResult);
                var pageResultObj = pageEnvelope.GetProperty("result");
                CollectDecisionCases(pageResultObj.GetProperty("cases"), recovered);
                foreach (var matrixCase in pageResultObj.GetProperty("cases").EnumerateArray())
                {
                    Assert.True(matrixCase.TryGetProperty("regression", out var regression));
                    Assert.Contains(regression.GetProperty("verdict").GetString(), new[] { "pass", "fail", "inconclusive" });
                    var id = matrixCase.GetProperty("artifactId").GetString()!;
                    Assert.True(ArtifactIds.IsValid(id));
                    Assert.IsType<ArtifactMetricsSection>(ArtifactReader.ReadSection(root, id, "metrics", owner));
                }

                cursor = pageResultObj.TryGetProperty("continuation", out var next) && next.ValueKind == JsonValueKind.Number
                    ? next.GetInt32()
                    : null;
            }

            Assert.Equal(32, recovered.Count);
            Assert.Equal(Enumerable.Range(0, 32), recovered.Select(item => item.Index));
            Assert.Equal(32, recovered.Select(item => item.ArtifactId).Distinct().Count());
            Assert.Equal("fail", recovered[7].Verdict);
            Assert.Equal("inconclusive", recovered[15].Verdict);
            Assert.Equal(30, recovered.Count(item => item.Verdict == "pass"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Matrix_omitted_cells_are_reachable_through_artifact_scope()
    {
        var owner = new ArtifactOwner("profile", new Dictionary<string, string>(), "sqlserver", "server", "db");
        var root = Path.Combine(Path.GetTempPath(), $"sqlharness-mcp-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var cells = Enumerable.Range(0, 100)
                .Select(i =>
                {
                    var id = $"matrixcell{i:D3}";
                    var directory = Path.Combine(root, id);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "manifest.json"),
                        "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"summary\",\"metrics\",\"operators\"],\"owner\":"
                        + JsonSerializer.Serialize(owner, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "}");
                    File.WriteAllText(Path.Combine(directory, "report.json"),
                        JsonSerializer.Serialize(BuildCompareReport($"cell-{i}"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    var parameterValue = i switch { 0 => "1", 1 => null, 2 => "20", _ => i.ToString() };
                    return new CompareMatrixCellReport(i, parameterValue, BuildCompareReport(new string('w', 5000), directory));
                })
                .ToArray();
            var matrix = new SqlHarnessCompareMatrixReport("batch", "int", cells);
            new CompareArtifactWriter(root, () => DateTimeOffset.UnixEpoch).WriteMatrixIndex(cells);
            var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null);
            var zeroProjection = AgentOutputProjection.Project(matrix, 128, 0, out _, 16384);
            var zeroProjectionBytes = JsonSerializer.SerializeToUtf8Bytes(zeroProjection, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
            Assert.True(zeroProjectionBytes < 16384, $"Zero-detail projection unexpectedly serializes to {zeroProjectionBytes} bytes.");

            var result = McpResultAdapter.Adapt(outcome, "sqlharness_compare", new McpResultBudget(16384, 128));

            Assert.False(result.IsError == true, Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
            Assert.True(McpResultAdapter.MeasureBytes(result) <= 16384,
                "Oversized matrix projection must fit the requested text budget.");
            var envelope = EnvelopeOf(result);
            AssertValidEnvelope(envelope);
            var projected = envelope.GetProperty("result");
            Assert.DoesNotContain(root, envelope.GetRawText(), StringComparison.OrdinalIgnoreCase);
            var projectedCases = projected.GetProperty("cases");
            Assert.True(projectedCases.GetArrayLength() < 100 || projected.GetProperty("omittedDetailCount").GetInt32() > 0,
                "An oversized matrix should preserve its case inventory before omitting optional detail.");
            Assert.True(envelope.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
            var matrixArtifactId = projected.GetProperty("matrixArtifactId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(matrixArtifactId), "Paged recovery must identify the scope-owned matrix index artifact.");
            Assert.Equal(JsonValueKind.Number, projected.GetProperty("continuation").ValueKind);
            var foreign = Assert.Throws<ArtifactReadException>(() => ArtifactReader.ReadSection(
                root, matrixArtifactId!, "matrix-cells", owner with { Profile = "foreign-profile" }, projected.GetProperty("continuation").GetInt32()));
            Assert.Equal("The artifact is not available in the current scope.", foreign.Message);
            var recovered = new List<int>();
            var recoveredValues = new List<string?>();
            foreach (var matrixCase in projectedCases.EnumerateArray())
            {
                var id = matrixCase.GetProperty("artifactId").GetString();
                Assert.False(string.IsNullOrEmpty(id));
                var section = ArtifactReader.ReadSection(root, id, "metrics", owner);
                Assert.IsType<ArtifactMetricsSection>(section);
                recovered.Add(matrixCase.GetProperty("index").GetInt32());
                recoveredValues.Add(matrixCase.GetProperty("parameterValue").ValueKind == JsonValueKind.Null
                    ? null
                    : matrixCase.GetProperty("parameterValue").GetString());
            }

            var cursor = projected.TryGetProperty("continuation", out var continuation) && continuation.ValueKind == JsonValueKind.Number
                ? continuation.GetInt32()
                : (int?)null;
            while (cursor is { } pageCursor)
            {
                var page = Assert.IsType<ArtifactMatrixCellsSection>(ArtifactReader.ReadSection(
                    root, matrixArtifactId!, "matrix-cells", owner, pageCursor));
                var pageOutcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, page, null);
                var pageResult = McpResultAdapter.Adapt(pageOutcome, "sqlharness_artifact", new McpResultBudget(16384, 128));
                Assert.True(McpResultAdapter.MeasureBytes(pageResult) <= 16384);
                var pageEnvelope = EnvelopeOf(pageResult);
                foreach (var matrixCase in pageEnvelope.GetProperty("result").GetProperty("cases").EnumerateArray())
                {
                    var artifactId = matrixCase.GetProperty("artifactId").GetString()!;
                    var section = ArtifactReader.ReadSection(root, artifactId, "metrics", owner);
                    Assert.IsType<ArtifactMetricsSection>(section);
                    recovered.Add(matrixCase.GetProperty("index").GetInt32());
                    recoveredValues.Add(matrixCase.GetProperty("parameterValue").ValueKind == JsonValueKind.Null
                        ? null
                        : matrixCase.GetProperty("parameterValue").GetString());
                }
                cursor = pageEnvelope.GetProperty("result").TryGetProperty("continuation", out var next) && next.ValueKind == JsonValueKind.Number
                    ? next.GetInt32()
                    : null;
            }

            Assert.Equal(Enumerable.Range(0, 100), recovered);
            Assert.Equal(Enumerable.Range(0, 100).Select(i => i switch { 0 => "1", 1 => null, 2 => "20", _ => i.ToString() }),
                recoveredValues);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public void All_adapted_results_fit_result_text_bytes(int maximumBytes)
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var text = string.Concat(Enumerable.Repeat("x", maximumBytes));
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(maximumBytes, 1));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(maximumBytes));

        Assert.True(McpResultAdapter.MeasureBytes(result) <= maximumBytes,
            $"Adapted result must fit the {maximumBytes} byte text budget.");
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.True(envelope.TryGetProperty("result", out _));
    }

    [Fact]
    public void Query_three_small_result_sets_stay_complete()
    {
        var report = QueryShapeFixtures.Report(QueryShapeFixtures.Set(2, 3), QueryShapeFixtures.Set(1, 1), QueryShapeFixtures.Set(3, 2));

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "sqlharness_query");

        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.DefaultCallToolResultBudgetBytes);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        AssertOmitted(envelope, "truncation");
        var projected = envelope.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(projected, report);
        var sets = projected.GetProperty("resultSets");
        Assert.Equal(3, sets.GetArrayLength());
        for (var i = 0; i < 3; i++)
            Assert.Equal(report.ResultSets[i].Rows.Count, sets[i].GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void Query_large_detail_set_keeps_summary_sets_within_text_budget()
    {
        var report = QueryShapeFixtures.Report(QueryShapeFixtures.Set(4, 1000, width: 40), QueryShapeFixtures.Set(2, 3), QueryShapeFixtures.Set(3, 2));

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "sqlharness_query");

        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.DefaultCallToolResultBudgetBytes);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        var truncation = envelope.GetProperty("truncation");
        Assert.Equal(JsonValueKind.Object, truncation.ValueKind);
        Assert.False(truncation.TryGetProperty("detailLimit", out _));
        Assert.True(truncation.GetProperty("maxCellChars").GetInt32() > 0);
        var projected = envelope.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(projected, report);
        var sets = projected.GetProperty("resultSets");
        Assert.Equal(3, sets.GetArrayLength());
        var detailRows = sets[0].GetProperty("rows").GetArrayLength();
        Assert.InRange(detailRows, 2, 999);
        Assert.Equal(3, sets[1].GetProperty("rows").GetArrayLength());
        Assert.Equal(2, sets[2].GetProperty("rows").GetArrayLength());
        Assert.Equal(1000 - detailRows, truncation.GetProperty("omittedItems").GetInt32());
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public void Query_wide_schema_and_many_sets_fall_back_within_text_budget(int maximumBytes)
    {
        var wide = QueryShapeFixtures.Report(QueryShapeFixtures.Set(400, 3, width: 5000, nameLength: 60), QueryShapeFixtures.Set(2, 2));
        var many = QueryShapeFixtures.Report(Enumerable.Range(0, 400).Select(_ => QueryShapeFixtures.Set(3, 50, width: 20)).ToArray());

        foreach (var report in new[] { wide, many })
        {
            var before = JsonSerializer.Serialize(report);
            var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "sqlharness_query",
                new McpResultBudget(maximumBytes));

            Assert.True(McpResultAdapter.MeasureBytes(result) <= maximumBytes);
            Assert.Equal(before, JsonSerializer.Serialize(report));
            var envelope = EnvelopeOf(result);
            AssertValidEnvelope(envelope);
            Assert.Equal("success", envelope.GetProperty("status").GetString());
            Assert.False(envelope.GetProperty("truncation").TryGetProperty("detailLimit", out _));
            var projected = envelope.GetProperty("result");
            Assert.False(projected.TryGetProperty("resultHash", out _));
            Assert.Equal("db", projected.GetProperty("database").GetString());
            QueryShapeFixtures.AssertAccounted(projected, report);
            Assert.True(projected.GetProperty("resultSets").GetArrayLength() > 0);
        }
    }

    [Fact]
    public void Query_wide_first_set_then_summary_set_at_level_zero_fits_text_budget_with_explicit_omissions()
    {
        const int budget = 4096;
        var report = QueryShapeFixtures.Report(QueryShapeFixtures.Set(400, 3, width: 5000, nameLength: 60), QueryShapeFixtures.Set(2, 2));

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "sqlharness_query",
            new McpResultBudget(budget));

        Assert.True(McpResultAdapter.MeasureBytes(result) <= budget);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.False(envelope.GetProperty("truncation").TryGetProperty("detailLimit", out _));
        var projected = envelope.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(projected, report);
        var sets = projected.GetProperty("resultSets");
        // The summary set's complete schema and rows are allocated before the wide
        // first schema's column prefix, which is explicitly cut and carries no rows.
        Assert.Equal(0, projected.TryGetProperty("omittedResultSetCount", out var omittedSets) ? omittedSets.GetInt32() : 0);
        Assert.Equal(2, sets.GetArrayLength());
        Assert.True(sets[0].GetProperty("omittedColumnCount").GetInt32() > 0);
        Assert.Equal(0, sets[0].GetProperty("rows").GetArrayLength());
        Assert.Equal(3, sets[0].GetProperty("omittedRowCount").GetInt64());
        QueryShapeFixtures.AssertSummariesComplete(projected, report, 1);
    }

    public static TheoryData<int, int, int> WideDetailShapes => new()
    {
        { 100, 200, 30 }, // SELECT * over ~100 columns: one row cannot fit
        { 12, 5000, 20 }, // ~12 nvarchar(max) columns, clipped to the cell limit
    };

    [Theory]
    [MemberData(nameof(WideDetailShapes))]
    public void Query_wide_one_row_detail_keeps_both_summaries_complete_within_default_text_budget(int columns, int width, int nameLength)
    {
        var report = QueryShapeFixtures.Report(
            QueryShapeFixtures.Set(columns, 1, width: width, nameLength: nameLength),
            QueryShapeFixtures.Set(1, 1),
            QueryShapeFixtures.Set(2, 5));
        var before = JsonSerializer.Serialize(report);

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "sqlharness_query");

        Assert.Equal(65536, McpLimits.DefaultCallToolResultBudgetBytes);
        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.DefaultCallToolResultBudgetBytes);
        Assert.Equal(before, JsonSerializer.Serialize(report));
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        var projected = envelope.GetProperty("result");
        Assert.False(projected.TryGetProperty("resultHash", out _));
        Assert.Equal("db", projected.GetProperty("database").GetString());
        QueryShapeFixtures.AssertAccounted(projected, report);
        QueryShapeFixtures.AssertSummariesComplete(projected, report, 1, 2);
        var detail = projected.GetProperty("resultSets")[0];
        Assert.Equal(columns, detail.GetProperty("columns").GetArrayLength()
            + (detail.TryGetProperty("omittedColumnCount", out var omittedColumns) ? omittedColumns.GetInt32() : 0));
    }

    private static void CollectDecisionCases(
        JsonElement cases,
        List<(int Index, string? ArtifactId, string? Verdict, string? ParameterValue)> recovered)
    {
        foreach (var matrixCase in cases.EnumerateArray())
        {
            Assert.True(matrixCase.TryGetProperty("regression", out var regression), "Paged cases must keep the verdict.");
            recovered.Add((
                matrixCase.GetProperty("index").GetInt32(),
                matrixCase.GetProperty("artifactId").GetString(),
                regression.GetProperty("verdict").GetString(),
                matrixCase.TryGetProperty("parameterValue", out var value) && value.ValueKind != JsonValueKind.Null
                    ? value.GetString()
                    : null));
        }
    }

    private static CompareMatrixCellReport MixedMatrixCell(string root, ArtifactOwner owner, int index)
    {
        var id = $"matrixcell{index:D3}";
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"summary\",\"metrics\",\"operators\"],\"owner\":"
            + JsonSerializer.Serialize(owner, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "}");
        var compare = MixedCompare(index, directory);
        File.WriteAllText(Path.Combine(directory, "report.json"),
            JsonSerializer.Serialize(compare, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new CompareMatrixCellReport(index, (index + 1).ToString(), compare);
    }

    private static SqlHarnessCompareReport MixedCompare(int index, string? artifactDirectory = null)
    {
        var verdict = index == 7 ? RegressionPolicy.Fail : index == 15 ? RegressionPolicy.Inconclusive : RegressionPolicy.Pass;
        var reasons = verdict switch
        {
            RegressionPolicy.Fail => (IReadOnlyList<string>)[RegressionPolicy.ReasonElapsedRegressed],
            RegressionPolicy.Inconclusive => [RegressionPolicy.ReasonUnstableElapsed],
            _ => [],
        };
        var mismatch = verdict == RegressionPolicy.Fail ? (IReadOnlyList<string>)[ResultMismatchReasons.Rows] : null;
        return BuildCompareReport($"cell-{index}", artifactDirectory) with
        {
            Equivalence = new ResultEquivalenceReport(
                ResultComparisonMode.Ordered, mismatch is null, mismatch is null ? 0 : 2, 0, 0,
                BaselineStable: true, CandidateStable: true, SchemaEquivalent: true, MismatchReasons: mismatch),
            Regression = new RegressionReport(
                verdict, reasons, RegressionPolicy.PolicyVersion,
                new RegressionDiagnostic("status", 10m, 12m, 2m, 20m),
                new RegressionDiagnostic("status", 1m, 1m, 0m, 0m),
                new RegressionDiagnostic("status", 1m, 1m, 0m, 0m)),
            Baseline = BuildCompareReport("b").Baseline with
            {
                ElapsedTimeMilliseconds = new CompareDistribution(index + 1, index + 10, index + 20),
                MetricReport = new BenchmarkMetricReport(
                    BenchmarkMetricReport.Measured, BenchmarkMetricReport.Measured,
                    new FractionalMilliseconds(index + 1, index + 10, index + 20),
                    true, null, null, BenchmarkMetricReport.Measured, null, null, null,
                    BenchmarkMetricReport.ResultStatement, BenchmarkMetricText.StatementRows, []),
            },
        };
    }

    private static SqlHarnessCompareReport BuildCompareReport(string marker, string? artifactDirectory = null) => new(
        new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile"),
        1, 1, true,
        new CompareVariantReport("baseline", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], [marker]),
        new CompareVariantReport("candidate", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []),
        artifactDirectory)
    {
        Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Multiset, true, null, 0, 0),
        Classification = new CompareClassificationReport("none", "read-only", "read-only"),
    };
}
