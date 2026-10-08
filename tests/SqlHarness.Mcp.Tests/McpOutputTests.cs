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
        Assert.Equal(6, envelope.GetProperty("exitCode").GetInt32());
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
        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.CallToolResultBudgetBytes,
            "Small query result must fit the default wire budget.");
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("truncation").ValueKind);
        var resultSets = envelope.GetProperty("result").GetProperty("resultSets");
        Assert.Equal(1, resultSets.GetArrayLength());
        Assert.Equal(3, resultSets[0].GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void Large_query_projection_truncates_within_real_wire_budget()
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

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        Assert.False(result.IsError == true);
        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.CallToolResultBudgetBytes,
            "Large query projection must still fit the real wire budget.");
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("truncation").ValueKind);
        Assert.True(envelope.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Matrix_omitted_cells_are_reachable_through_artifact_scope()
    {
        var owner = new ArtifactOwner("profile", new Dictionary<string, string>(), "sqlserver", "server", "db");
        var root = Path.Combine(Path.GetTempPath(), $"sqlharness-mcp-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var cells = Enumerable.Range(0, 20)
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
                    return new CompareMatrixCellReport(i, i.ToString(), BuildCompareReport(new string('w', 5000), directory));
                })
                .ToArray();
            var matrix = new SqlHarnessCompareMatrixReport("batch", "int", cells);
            var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null);

            var result = McpResultAdapter.Adapt(outcome, "sqlharness_compare", new McpResultBudget(8192, 128));

            Assert.False(result.IsError == true);
            Assert.True(McpResultAdapter.MeasureBytes(result) <= 8192,
                "Oversized matrix projection must fit the requested wire budget.");
            var envelope = EnvelopeOf(result);
            AssertValidEnvelope(envelope);
            var projected = envelope.GetProperty("result");
            var projectedCells = projected.GetProperty("cells");
            var references = projected.GetProperty("omittedCellReferences");
            Assert.True(projectedCells.GetArrayLength() < 20 || references.GetArrayLength() > 0,
                "An oversized matrix should either project fewer than 20 full cells or emit references for omitted cells.");
            Assert.True(envelope.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
            Assert.True(references.GetArrayLength() > 0, "Omitted cell references must be emitted so the caller can retrieve detail.");
            foreach (var reference in references.EnumerateArray())
            {
                var artifactDirectory = reference.GetProperty("artifactDirectory").GetString();
                Assert.False(string.IsNullOrEmpty(artifactDirectory));
                var id = Path.GetFileName(artifactDirectory)!;
                var section = ArtifactReader.ReadSection(root, id, "metrics", owner);
                Assert.IsType<ArtifactMetricsSection>(section);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public void All_adapted_results_fit_real_wire_bytes(int maximumBytes)
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var text = string.Concat(Enumerable.Repeat("x", maximumBytes));
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(maximumBytes, 1));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(maximumBytes));

        Assert.True(McpResultAdapter.MeasureBytes(result) <= maximumBytes,
            $"Adapted result must fit the {maximumBytes} byte wire budget.");
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.True(envelope.TryGetProperty("result", out _));
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