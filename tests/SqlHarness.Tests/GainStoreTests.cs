using SqlHarness.Core;

namespace SqlHarness.Tests;

[Collection(SqlHarnessHomeCollection.Name)]
public class GainStoreTests
{
    [Fact]
    public void Plan_and_schema_records_are_valid_and_contribute_to_total()
    {
        using var temp = new TempDirectory("plan-schema");
        var store = new GainStore(temp.FilePath);
        store.Append(Record("plan", true, 1, 4, 1, 0, 0, 1, 0, 1));
        store.Append(Record("schema", true, 1, 4, 1, 0, 0, 1, 0, 1));

        Assert.Equal(2, store.Aggregate().Total.Executions);
    }

    [Fact]
    public void Invalid_commands_and_inconsistent_token_counters_are_rejected()
    {
        using var temp = new TempDirectory("gain");
        var store = new GainStore(temp.FilePath);
        var consistent = Record("query", true, 1, 5, 1, 4, 1, 2, 1, 1);

        Assert.Throws<ArgumentException>(() => store.Append(consistent with { Command = "SELECT secret" }));
        Assert.Throws<ArgumentException>(() => store.Append(consistent with { RawEstimatedTokens = 1 }));
        Assert.Throws<ArgumentException>(() => store.Append(consistent with { EmittedEstimatedTokens = 2 }));
        Assert.Throws<ArgumentException>(() => store.Append(consistent with { SavedEstimatedTokens = 0 }));
        Assert.False(File.Exists(temp.FilePath));
    }

    [Fact]
    public void Append_and_aggregate_return_totals_and_command_breakdown()
    {
        using var temp = new TempDirectory("gain");
        var store = new GainStore(temp.FilePath);
        store.Append(Record("query", true, 10, 100, 10, 20, 2, 25, 5, 20));
        store.Append(Record("compare", false, 30, 5, 1, 10, 1, 2, 3, 0));
        store.Append(Record("measure", true, 40, 8, 2, 4, 1, 2, 1, 1));

        var report = store.Aggregate();

        Assert.Equal(new SqlHarnessGainSummary(3, 1, 80, 113, 13, 34, 4, 29, 9, 21), report.Total);
        Assert.Equal(1, report.Query.Executions);
        Assert.Equal(1, report.Compare.Executions);
        Assert.Equal(1, report.Measure.Executions);
        Assert.Equal(21d / 29d * 100d, report.Total.SavingsPercentage, 10);
    }

    [Fact]
    public void Legacy_perf_records_are_rejected()
    {
        using var temp = new TempDirectory("gain");
        var store = new GainStore(temp.FilePath);

        Assert.Throws<ArgumentException>(() =>
            store.Append(Record("perf", true, 1, 4, 1, 0, 0, 1, 0, 1)));
        Assert.False(File.Exists(temp.FilePath));
    }

    [Fact]
    public void Legacy_perf_records_already_on_disk_are_rejected()
    {
        using var temp = new TempDirectory("gain");
        File.WriteAllText(temp.FilePath,
            "{\"timestamp\":\"2026-07-13T10:20:30.0000000+00:00\",\"command\":\"perf\",\"success\":true,\"durationMilliseconds\":1,\"rawBytes\":4,\"rawLines\":1,\"emittedBytes\":0,\"emittedLines\":0,\"rawEstimatedTokens\":1,\"emittedEstimatedTokens\":0,\"savedEstimatedTokens\":1}\n");

        Assert.Throws<ArgumentException>(() => new GainStore(temp.FilePath).Aggregate());
    }

    [Fact]
    public void Jsonl_is_compact_and_concurrent_appends_remain_aggregatable()
    {
        using var temp = new TempDirectory("gain");
        var stores = Enumerable.Range(0, 4).Select(_ => new GainStore(temp.FilePath)).ToArray();

        Parallel.For(0, 100, i =>
            stores[i % stores.Length].Append(Record("compare", true, 1, 4, 1, 0, 0, 1, 0, 1)));

        var text = File.ReadAllText(temp.FilePath);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", text, StringComparison.Ordinal);
        Assert.Equal(100, File.ReadAllLines(temp.FilePath).Length);
        Assert.Equal(100, stores[0].Aggregate().Compare.Executions);
    }

    private static GainRecord Record(
        string command, bool success, long duration, long rawBytes, long rawLines,
        long emittedBytes, long emittedLines, long rawTokens, long emittedTokens, long savedTokens) =>
        new(new DateTimeOffset(2026, 7, 13, 10, 20, 30, TimeSpan.Zero), command, success,
            duration, rawBytes, rawLines, emittedBytes, emittedLines, rawTokens, emittedTokens, savedTokens);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sqlharness-{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string FilePath => System.IO.Path.Combine(Path, "gain.jsonl");
        public void Dispose() => Directory.Delete(Path, true);
    }
}