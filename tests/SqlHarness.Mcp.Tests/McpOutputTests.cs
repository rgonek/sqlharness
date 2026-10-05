using System.Text.Json;

using ModelContextProtocol.Protocol;

using SqlHarness.Core;

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
        Assert.Equal(text, result.StructuredContent?.GetRawText());
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static void AssertValidEnvelope(JsonElement envelope) =>
        Assert.Empty(McpResultAdapter.ValidateEnvelope(envelope));

    [Fact]
    public void Success_carries_a_valid_envelope_without_error()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_inspect");

        Assert.False(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("sqlharness_inspect", envelope.GetProperty("command").GetString());
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        Assert.Equal(0, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("error").ValueKind);
        Assert.Equal("srv", envelope.GetProperty("result").GetProperty("server").GetString());
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
        Assert.Equal(5, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal("Cell 2 failed.", envelope.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("batch", envelope.GetProperty("result").GetProperty("parameterName").GetString());
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
        Assert.Equal(5, envelope.GetProperty("exitCode").GetInt32());
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
        Assert.Equal((int)exitCode, envelope.GetProperty("exitCode").GetInt32());
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
            [BenchmarkMetricText.PostgresCpuUnavailable]);
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
        Assert.Equal(0, projected.GetProperty("cpuTimeMilliseconds").GetProperty("median").GetInt64());
        Assert.Contains(
            BenchmarkMetricText.PostgresCpuUnavailable,
            projected.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!),
            StringComparer.Ordinal);
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
        Assert.Equal(8, envelope.GetProperty("exitCode").GetInt32());
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
        Assert.Equal(7, envelope.GetProperty("exitCode").GetInt32());
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
}