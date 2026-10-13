using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

using SqlHarness.Core;
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

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        Assert.False(result.IsError == true);
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal(2, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("sqlharness_query", envelope.GetProperty("command").GetString());
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        Assert.Equal(0, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("error").ValueKind);
        Assert.Equal("srv", envelope.GetProperty("result").GetProperty("server").GetString());
    }

    [Fact]
    public void Compare_result_carries_the_nullability_diagnostic()
    {
        var report = BuildCompareReport("m") with
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Ordered, false, 0, 0, 0, true, false, ResultMismatchReasons.Nullability),
        };
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var envelope = EnvelopeOf(McpResultAdapter.Adapt(outcome, "sqlharness_compare"));

        var equivalence = envelope.GetProperty("result").GetProperty("equivalence");
        Assert.True(equivalence.GetProperty("compareNullability").GetBoolean());
        Assert.False(equivalence.GetProperty("nullabilityEquivalent").GetBoolean());
        Assert.Equal("nullability", equivalence.GetProperty("mismatchReason").GetString());
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
        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.DefaultCallToolResultBudgetBytes,
            "Small query result must fit the default text budget.");
        var envelope = EnvelopeOf(result);
        AssertValidEnvelope(envelope);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("truncation").ValueKind);
        var resultSets = envelope.GetProperty("result").GetProperty("resultSets");
        Assert.Equal(1, resultSets.GetArrayLength());
        Assert.Equal(3, resultSets[0].GetProperty("rows").GetArrayLength());
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
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("truncation").ValueKind);
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
        Assert.True(truncation.GetProperty("detailLimit").GetInt32() > 1);
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
            Assert.Equal(0, envelope.GetProperty("truncation").GetProperty("detailLimit").GetInt32());
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
        Assert.Equal(0, envelope.GetProperty("truncation").GetProperty("detailLimit").GetInt32());
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