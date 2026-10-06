using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ResultEquivalenceTests
{
    [Fact]
    public void Comparison_capture_preserves_order_and_duplicates()
    {
        using var capture = new CanonicalComparisonAccumulator();
        capture.BeginResultSet([new(0, "Id", "System.Int32", false)]);
        capture.AddRow([1]);
        capture.AddRow([1]);
        capture.AddRow([2]);
        capture.EndResultSet();

        var result = capture.Complete();

        Assert.Equal(3, result.OrderedRows.Count);
        Assert.Equal(result.OrderedRows[0], result.OrderedRows[1]);
        Assert.NotEqual(result.OrderedRows[1], result.OrderedRows[2]);
    }

    [Fact]
    public void Comparison_schema_hash_reflects_column_metadata()
    {
        using var originalCapture = new CanonicalComparisonAccumulator();
        originalCapture.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        originalCapture.AddRow([1]);
        originalCapture.EndResultSet();
        var original = originalCapture.Complete();

        using var renamedCapture = new CanonicalComparisonAccumulator();
        renamedCapture.BeginResultSet([new CanonicalColumn(0, "Other", "System.Int32", false)]);
        renamedCapture.AddRow([1]);
        renamedCapture.EndResultSet();
        var renamed = renamedCapture.Complete();

        using var retypedCapture = new CanonicalComparisonAccumulator();
        retypedCapture.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int64", false)]);
        retypedCapture.AddRow([1]);
        retypedCapture.EndResultSet();
        var retyped = retypedCapture.Complete();

        using var nullableCapture = new CanonicalComparisonAccumulator();
        nullableCapture.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", true)]);
        nullableCapture.AddRow([1]);
        nullableCapture.EndResultSet();
        var nullable = nullableCapture.Complete();

        Assert.NotEqual(original.SchemaHash, renamed.SchemaHash);
        Assert.NotEqual(original.SchemaHash, retyped.SchemaHash);
        Assert.NotEqual(original.SchemaHash, nullable.SchemaHash);
        Assert.Equal(original.OrderedRows, renamed.OrderedRows);
    }

    [Fact]
    public void Comparison_row_fingerprints_include_result_set_ordinal()
    {
        using var oneSet = new CanonicalComparisonAccumulator();
        oneSet.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        oneSet.AddRow([1]);
        oneSet.AddRow([2]);
        oneSet.EndResultSet();
        var combined = oneSet.Complete();

        using var twoSets = new CanonicalComparisonAccumulator();
        twoSets.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        twoSets.AddRow([1]);
        twoSets.EndResultSet();
        twoSets.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        twoSets.AddRow([2]);
        twoSets.EndResultSet();
        var split = twoSets.Complete();

        Assert.NotEqual(combined.SchemaHash, split.SchemaHash);
        Assert.Equal(2, combined.OrderedRows.Count);
        Assert.Equal(2, split.OrderedRows.Count);
        Assert.NotEqual(combined.OrderedRows[1], split.OrderedRows[1]);
        Assert.Equal(combined.OrderedRows[0], split.OrderedRows[0]);
    }

    [Fact]
    public void Comparison_capture_rejects_rows_beyond_configured_limit()
    {
        using var capture = new CanonicalComparisonAccumulator(maximumRows: 2);
        capture.BeginResultSet([new CanonicalColumn(0, "Id", "System.Int32", false)]);
        capture.AddRow([1]);
        capture.AddRow([2]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() => capture.AddRow([3]));

        Assert.Equal(
            "Result comparison exceeds the 1000000-row limit.",
            exception.Message);
    }

    [Fact]
    public void Comparison_capture_does_not_store_messages_or_values()
    {
        using var capture = new CanonicalComparisonAccumulator();
        capture.BeginResultSet([new CanonicalColumn(0, "Secret", "System.String", false)]);
        capture.AddRow(["never-expose-me"]);
        capture.EndResultSet();

        var result = capture.Complete();

        Assert.All(result.OrderedRows, fingerprint =>
        {
            Assert.DoesNotContain("never-expose-me", fingerprint, StringComparison.Ordinal);
            Assert.Matches("^[0-9A-F]{64}$", fingerprint);
        });
        Assert.DoesNotContain("never-expose-me", result.SchemaHash, StringComparison.Ordinal);
        Assert.Matches("^[0-9A-F]{64}$", result.SchemaHash);
    }

    [Fact]
    public void Maximum_compared_rows_constant_is_one_million()
    {
        Assert.Equal(1_000_000, CanonicalComparisonAccumulator.MaximumComparedRows);
    }

    [Theory]
    [InlineData(ResultComparisonMode.Ordered, false)]
    [InlineData(ResultComparisonMode.Multiset, true)]
    [InlineData(ResultComparisonMode.Set, true)]
    public void Modes_apply_order_and_duplicate_semantics(ResultComparisonMode mode, bool expected)
    {
        var report = AssertOracleAgrees(mode, [Capture("A", "A", "B")], [Capture("B", "A", "A")]);
        Assert.Equal(expected, report.Equivalent);
    }

    [Theory]
    [InlineData(ResultComparisonMode.Multiset, false)]
    [InlineData(ResultComparisonMode.Set, true)]
    public void Modes_apply_duplicate_multiplicity_semantics(ResultComparisonMode mode, bool expected)
    {
        var report = AssertOracleAgrees(mode, [Capture("A", "A", "B")], [Capture("A", "B")]);
        Assert.Equal(expected, report.Equivalent);
    }

    [Fact]
    public void Ordered_order_only_change_reports_positions_with_zero_directional_counts()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Ordered,
            [Capture("A", "A", "B")],
            [Capture("B", "A", "A")]);

        Assert.False(report.Equivalent);
        Assert.True(report.DifferingPositions > 0);
        Assert.Equal(0, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
    }

    [Fact]
    public void Multiset_missing_duplicate_reports_baseline_only_count()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Multiset,
            [Capture("A", "A", "B")],
            [Capture("A", "B")]);

        Assert.False(report.Equivalent);
        Assert.Equal(1, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
        Assert.Null(report.DifferingPositions);
    }

    [Fact]
    public void Schema_mismatch_is_not_equivalent()
    {
        var baseline = new CanonicalComparisonResult("schema-a", ["A"]);
        var candidate = new CanonicalComparisonResult("schema-b", ["A"]);

        var ordered = AssertOracleAgrees(ResultComparisonMode.Ordered, [baseline], [candidate]);
        var multiset = AssertOracleAgrees(ResultComparisonMode.Multiset, [baseline], [candidate]);
        var set = AssertOracleAgrees(ResultComparisonMode.Set, [baseline], [candidate]);

        Assert.False(ordered.Equivalent);
        Assert.False(multiset.Equivalent);
        Assert.False(set.Equivalent);
    }

    [Fact]
    public void Multiple_repetitions_require_every_run_to_match_first_baseline()
    {
        var stable = Capture("A", "A", "B");
        var changed = Capture("A", "B", "B");

        var report = AssertOracleAgrees(
            ResultComparisonMode.Ordered,
            [stable, stable, changed],
            [stable, stable]);

        Assert.False(report.Equivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Mode);
    }

    [Fact]
    public void Multiple_repetitions_report_maximum_observed_pair_counts()
    {
        var match = Capture("A", "A", "B");
        var reordered = Capture("B", "A", "A");
        var missingDuplicate = Capture("A", "B");

        var report = AssertOracleAgrees(
            ResultComparisonMode.Ordered,
            [match, match],
            [reordered, missingDuplicate]);

        Assert.False(report.Equivalent);
        // reordered pair: positions differ, directional 0; missing-duplicate pair: directional baseline-only 1
        Assert.True(report.DifferingPositions >= 2);
        Assert.Equal(1, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
    }

    [Fact]
    public void Off_mode_nulls_all_result_fields_except_mode()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Off,
            [Capture("A")],
            [Capture("B")]);

        Assert.Equal(ResultComparisonMode.Off, report.Mode);
        Assert.Null(report.Equivalent);
        Assert.Null(report.DifferingPositions);
        Assert.Null(report.BaselineOnlyCount);
        Assert.Null(report.CandidateOnlyCount);
    }

    [Fact]
    public void Set_mode_reports_unique_directional_counts()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Set,
            [Capture("A", "A", "B")],
            [Capture("B", "C")]);

        Assert.False(report.Equivalent);
        Assert.Equal(1, report.BaselineOnlyCount); // unique A
        Assert.Equal(1, report.CandidateOnlyCount); // unique C
        Assert.Null(report.DifferingPositions);
    }

    [Fact]
    public void Ordered_reorder_reports_exact_differing_positions()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Ordered,
            [new CanonicalComparisonResult("schema", ["A", "B"])],
            [new CanonicalComparisonResult("schema", ["B", "A"])]);

        Assert.False(report.Equivalent);
        Assert.Equal(2, report.DifferingPositions);
        Assert.Equal(0, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
    }

    [Fact]
    public void Ordered_differing_positions_include_length_difference()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Ordered,
            [new CanonicalComparisonResult("schema", ["A", "B", "C"])],
            [new CanonicalComparisonResult("schema", ["A", "X"])]);

        Assert.False(report.Equivalent);
        Assert.Equal(2, report.DifferingPositions);
        Assert.Equal(2, report.BaselineOnlyCount);
        Assert.Equal(1, report.CandidateOnlyCount);
    }

    [Fact]
    public void Multiset_duplicate_multiplicity_is_significant()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Multiset,
            [new CanonicalComparisonResult("schema", ["1", "1", "2"])],
            [new CanonicalComparisonResult("schema", ["1", "2", "2"])]);

        Assert.False(report.Equivalent);
        Assert.Equal(1, report.BaselineOnlyCount);
        Assert.Equal(1, report.CandidateOnlyCount);
        Assert.Null(report.DifferingPositions);
    }

    [Fact]
    public void Set_duplicates_are_ignored()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Set,
            [new CanonicalComparisonResult("schema", ["1", "1", "2"])],
            [new CanonicalComparisonResult("schema", ["1", "2"])]);

        Assert.True(report.Equivalent);
        Assert.Equal(0, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
        Assert.Null(report.DifferingPositions);
    }

    [Fact]
    public void Schema_mismatch_still_reports_directional_row_counts()
    {
        var baseline = new CanonicalComparisonResult("schema-a", ["A", "B"]);
        var candidate = new CanonicalComparisonResult("schema-b", ["A", "C"]);

        var ordered = AssertOracleAgrees(ResultComparisonMode.Ordered, [baseline], [candidate]);
        Assert.False(ordered.Equivalent);
        Assert.Equal(1, ordered.DifferingPositions);
        Assert.Equal(1, ordered.BaselineOnlyCount);
        Assert.Equal(1, ordered.CandidateOnlyCount);

        var multiset = AssertOracleAgrees(ResultComparisonMode.Multiset, [baseline], [candidate]);
        Assert.False(multiset.Equivalent);
        Assert.Equal(1, multiset.BaselineOnlyCount);
        Assert.Equal(1, multiset.CandidateOnlyCount);

        var set = AssertOracleAgrees(ResultComparisonMode.Set, [baseline], [candidate]);
        Assert.False(set.Equivalent);
        Assert.Equal(1, set.BaselineOnlyCount);
        Assert.Equal(1, set.CandidateOnlyCount);
    }

    [Theory]
    [InlineData(ResultComparisonMode.Ordered)]
    [InlineData(ResultComparisonMode.Multiset)]
    [InlineData(ResultComparisonMode.Set)]
    public void Empty_results_with_same_schema_are_equivalent(ResultComparisonMode mode)
    {
        var report = AssertOracleAgrees(
            mode,
            [new CanonicalComparisonResult("schema", [])],
            [new CanonicalComparisonResult("schema", [])]);

        Assert.True(report.Equivalent);
        Assert.Equal(0, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
        Assert.Equal(mode == ResultComparisonMode.Ordered ? (long?)0 : null, report.DifferingPositions);
    }

    [Fact]
    public void Empty_versus_nonempty_is_not_equivalent()
    {
        var empty = new CanonicalComparisonResult("schema", []);
        var nonempty = new CanonicalComparisonResult("schema", ["A"]);

        var missing = AssertOracleAgrees(ResultComparisonMode.Ordered, [empty], [nonempty]);
        Assert.False(missing.Equivalent);
        Assert.Equal(1, missing.DifferingPositions);
        Assert.Equal(0, missing.BaselineOnlyCount);
        Assert.Equal(1, missing.CandidateOnlyCount);

        var extra = AssertOracleAgrees(ResultComparisonMode.Ordered, [nonempty], [empty]);
        Assert.False(extra.Equivalent);
        Assert.Equal(1, extra.DifferingPositions);
        Assert.Equal(1, extra.BaselineOnlyCount);
        Assert.Equal(0, extra.CandidateOnlyCount);
    }

    [Fact]
    public void Unstable_baseline_runs_are_not_equivalent()
    {
        var run0 = new CanonicalComparisonResult("schema", ["1"]);
        var run1 = new CanonicalComparisonResult("schema", ["2"]);

        var report = AssertOracleAgrees(ResultComparisonMode.Ordered, [run0, run1], [run0]);

        Assert.False(report.Equivalent);
        Assert.Equal(1, report.BaselineOnlyCount);
        Assert.Equal(1, report.CandidateOnlyCount);
    }

    [Fact]
    public void Maximum_baseline_only_count_comes_from_all_pairs()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Multiset,
            [new CanonicalComparisonResult("schema", ["A", "B"])],
            [new CanonicalComparisonResult("schema", ["C"]), new CanonicalComparisonResult("schema", ["A"])]);

        Assert.False(report.Equivalent);
        Assert.Equal(2, report.BaselineOnlyCount);
        Assert.Equal(1, report.CandidateOnlyCount);
    }

    [Fact]
    public void Maximum_candidate_only_count_comes_from_all_pairs()
    {
        var report = AssertOracleAgrees(
            ResultComparisonMode.Multiset,
            [new CanonicalComparisonResult("schema", ["A"])],
            [new CanonicalComparisonResult("schema", ["B", "C"]), new CanonicalComparisonResult("schema", ["A"])]);

        Assert.False(report.Equivalent);
        Assert.Equal(1, report.BaselineOnlyCount);
        Assert.Equal(2, report.CandidateOnlyCount);
    }

    [Fact]
    public void Randomized_sweep_agrees_with_oracle_and_budget_seam_preserves_semantics()
    {
        var random = new Random(42);
        string[] alphabet = ["a", "b", "c", "d", "e"];
        var modes = new[] { ResultComparisonMode.Ordered, ResultComparisonMode.Multiset, ResultComparisonMode.Set };

        for (var kase = 0; kase < 500; kase++)
        {
            var mode = modes[random.Next(modes.Length)];
            // Baseline stays non-empty: an empty baseline throws ArgumentException (T1 behavior).
            var baseline = RandomRuns(random, alphabet, random.Next(1, 4));
            var candidate = RandomRuns(random, alphabet, random.Next(0, 4));

            var expected = ReferenceCompare(mode, baseline, candidate);
            Assert.Equal(expected, ResultComparer.Compare(mode, baseline, candidate));
            Assert.Equal(
                expected,
                ResultComparer.Compare(mode, baseline, candidate, maxUniqueFingerprints: 1_000_000, CancellationToken.None));
        }
    }

    [Fact]
    public void Comparison_budget_rejects_more_unique_fingerprints_than_allowed()
    {
        var baseline = new List<CanonicalComparisonResult> { Capture("A", "B") };
        var candidate = new List<CanonicalComparisonResult> { Capture("A", "B") };

        var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
            ResultComparer.Compare(
                ResultComparisonMode.Ordered, baseline, candidate,
                maxUniqueFingerprints: 1, CancellationToken.None));

        Assert.Contains("comparison budget", exception.Message, StringComparison.Ordinal);

        // The same data fits a sufficient budget and agrees with the oracle.
        AssertBudgetSeamAgrees(ResultComparisonMode.Ordered, baseline, candidate, maxUniqueFingerprints: 2);
    }

    [Fact]
    public void Comparison_budget_counts_identical_runs_once()
    {
        var run = Capture("A", "B");
        var baseline = new List<CanonicalComparisonResult> { run, run };
        var candidate = new List<CanonicalComparisonResult> { run, run };

        // Two identical 2-row runs register once: 2 unique fingerprints fit a budget of 2.
        var report = AssertBudgetSeamAgrees(
            ResultComparisonMode.Multiset, baseline, candidate, maxUniqueFingerprints: 2);

        Assert.True(report.Equivalent);
    }

    [Fact]
    public void Compare_with_cancelled_token_throws_operation_canceled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ResultComparer.Compare(ResultComparisonMode.Ordered, [Capture("A")], [Capture("A")], source.Token));
    }

    [Fact]
    public async Task Compare_observes_cancellation_started_mid_flight()
    {
        // 90 baseline + 2 candidate runs x 20000 rows drawn from 10 distinct fingerprints:
        // runs differ, so the registry holds ~1.84M row references (~15 MB) while only
        // 10 unique fingerprints exist, leaving the 2M default budget untouched — only
        // timing scales, never the budget. Row/run counts (not unique fingerprints) were
        // calibrated up until a 1 ms CancelAfter is observed reliably. The test asserts
        // only OperationCanceledException, never timing.
        var random = new Random(1234);
        var baseline = RandomWideRuns(random, 90);
        var candidate = RandomWideRuns(random, 2);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var source = new CancellationTokenSource();
            source.CancelAfter(TimeSpan.FromMilliseconds(1));
            try
            {
                await Task.Run(() => ResultComparer.Compare(
                    ResultComparisonMode.Multiset, baseline, candidate, source.Token),
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        Assert.Fail("Compare did not observe mid-flight cancellation within 10 attempts.");
    }

    [Fact]
    public void Compare_allocates_below_cap_on_bounded_synthetic_set()
    {
        // Cap calibration: measured 15_080 bytes allocated on this thread for this
        // fixed set (10 baseline + 10 candidate runs x 1000 rows, 5 unique fingerprints,
        // Multiset mode, `dotnet test` Debug on .NET 8 x64); the 1_000_000 cap keeps
        // ~66x headroom (>=4x required) to catch a return of per-pair allocations
        // (the pre-dedup code rebuilt histograms per pair, i.e. megabytes here),
        // not machine noise.
        const long AllocationCapBytes = 1_000_000;
        var baseline = RandomRuns(new Random(7), ["a", "b", "c", "d", "e"], 10, 1000);
        var candidate = RandomRuns(new Random(11), ["a", "b", "c", "d", "e"], 10, 1000);

        ResultComparer.Compare(ResultComparisonMode.Multiset, baseline, candidate);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var report = ResultComparer.Compare(ResultComparisonMode.Multiset, baseline, candidate);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(ReferenceCompare(ResultComparisonMode.Multiset, baseline, candidate), report);
        Assert.True(
            allocated < AllocationCapBytes,
            $"Compare allocated {allocated:N0} bytes, above the {AllocationCapBytes:N0}-byte cap.");
    }

    [Fact]
    public void Off_mode_returns_early_with_cancelled_token()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var report = ResultComparer.Compare(
            ResultComparisonMode.Off, [Capture("A")], [Capture("B")], source.Token);

        Assert.Equal(ResultComparisonMode.Off, report.Mode);
        Assert.Null(report.Equivalent);
        Assert.Null(report.DifferingPositions);
        Assert.Null(report.BaselineOnlyCount);
        Assert.Null(report.CandidateOnlyCount);
    }

    private static List<CanonicalComparisonResult> RandomRuns(
        Random random, string[] alphabet, int runCount, int? fixedLength = null)
    {
        var runs = new List<CanonicalComparisonResult>(runCount);
        for (var index = 0; index < runCount; index++)
        {
            var length = fixedLength ?? random.Next(0, 6);
            var rows = new string[length];
            for (var row = 0; row < length; row++)
                rows[row] = alphabet[random.Next(alphabet.Length)];
            var schema = random.Next(10) == 0 ? "other-schema" : "schema";
            runs.Add(new CanonicalComparisonResult(schema, rows));
        }

        return runs;
    }

    private static List<CanonicalComparisonResult> RandomWideRuns(Random random, int runCount)
    {
        const int RowsPerRun = 20000;
        const int UniqueFingerprints = 10;
        var runs = new List<CanonicalComparisonResult>(runCount);
        for (var index = 0; index < runCount; index++)
        {
            var rows = new string[RowsPerRun];
            for (var row = 0; row < RowsPerRun; row++)
                rows[row] = "fp-" + random.Next(UniqueFingerprints);
            runs.Add(new CanonicalComparisonResult("schema", rows));
        }

        return runs;
    }

    private static ResultEquivalenceReport AssertBudgetSeamAgrees(
        ResultComparisonMode mode,
        IReadOnlyList<CanonicalComparisonResult> baseline,
        IReadOnlyList<CanonicalComparisonResult> candidate,
        int maxUniqueFingerprints)
    {
        var actual = ResultComparer.Compare(mode, baseline, candidate, maxUniqueFingerprints, CancellationToken.None);
        Assert.Equal(ReferenceCompare(mode, baseline, candidate), actual);
        return actual;
    }

    private static ResultEquivalenceReport AssertOracleAgrees(
        ResultComparisonMode mode,
        IReadOnlyList<CanonicalComparisonResult> baseline,
        IReadOnlyList<CanonicalComparisonResult> candidate)
    {
        var actual = ResultComparer.Compare(mode, baseline, candidate);
        Assert.Equal(ReferenceCompare(mode, baseline, candidate), actual);
        return actual;
    }

    private static ResultEquivalenceReport ReferenceCompare(
        ResultComparisonMode mode,
        IReadOnlyList<CanonicalComparisonResult> baseline,
        IReadOnlyList<CanonicalComparisonResult> candidate)
    {
        if (mode == ResultComparisonMode.Off)
            return new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null);

        var reference = baseline[0];
        var equivalent = true;
        foreach (var run in baseline)
        {
            if (!ReferenceEquivalent(mode, reference, run))
                equivalent = false;
        }

        foreach (var run in candidate)
        {
            if (!ReferenceEquivalent(mode, reference, run))
                equivalent = false;
        }

        long? maxDifferingPositions = mode == ResultComparisonMode.Ordered ? 0 : null;
        long maxBaselineOnly = 0;
        long maxCandidateOnly = 0;
        foreach (var left in baseline)
        {
            foreach (var right in candidate)
            {
                if (maxDifferingPositions is not null)
                    maxDifferingPositions = Math.Max(
                        maxDifferingPositions.Value,
                        ReferenceDifferingPositions(left.OrderedRows, right.OrderedRows));
                var (baselineOnly, candidateOnly) = mode == ResultComparisonMode.Set
                    ? ReferenceSetCounts(left.OrderedRows, right.OrderedRows)
                    : ReferenceMultisetCounts(left.OrderedRows, right.OrderedRows);
                maxBaselineOnly = Math.Max(maxBaselineOnly, baselineOnly);
                maxCandidateOnly = Math.Max(maxCandidateOnly, candidateOnly);
            }
        }

        return new ResultEquivalenceReport(mode, equivalent, maxDifferingPositions, maxBaselineOnly, maxCandidateOnly);
    }

    private static bool ReferenceEquivalent(
        ResultComparisonMode mode,
        CanonicalComparisonResult left,
        CanonicalComparisonResult right)
    {
        if (!string.Equals(left.SchemaHash, right.SchemaHash, StringComparison.Ordinal))
            return false;

        if (mode == ResultComparisonMode.Ordered)
            return ReferenceOrderedEqual(left.OrderedRows, right.OrderedRows);
        if (mode == ResultComparisonMode.Multiset)
        {
            var (baselineOnly, candidateOnly) = ReferenceMultisetCounts(left.OrderedRows, right.OrderedRows);
            return baselineOnly == 0 && candidateOnly == 0;
        }

        if (mode == ResultComparisonMode.Set)
        {
            var (baselineOnly, candidateOnly) = ReferenceSetCounts(left.OrderedRows, right.OrderedRows);
            return baselineOnly == 0 && candidateOnly == 0;
        }

        throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported result comparison mode.");
    }

    private static bool ReferenceOrderedEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static long ReferenceDifferingPositions(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var shared = Math.Min(left.Count, right.Count);
        long differing = Math.Abs(left.Count - right.Count);
        for (var index = 0; index < shared; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                differing++;
        }

        return differing;
    }

    private static (long BaselineOnly, long CandidateOnly) ReferenceMultisetCounts(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var unmatched = new List<string>(right);
        long baselineOnly = 0;
        foreach (var fingerprint in left)
        {
            if (!unmatched.Remove(fingerprint))
                baselineOnly++;
        }

        return (baselineOnly, unmatched.Count);
    }

    private static (long BaselineOnly, long CandidateOnly) ReferenceSetCounts(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);
        long baselineOnly = 0;
        foreach (var fingerprint in leftSet)
        {
            if (!rightSet.Contains(fingerprint))
                baselineOnly++;
        }

        long candidateOnly = 0;
        foreach (var fingerprint in rightSet)
        {
            if (!leftSet.Contains(fingerprint))
                candidateOnly++;
        }

        return (baselineOnly, candidateOnly);
    }

    private static CanonicalComparisonResult Capture(params string[] rows) =>
        new("schema", rows);
}