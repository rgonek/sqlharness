using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class ParameterInputFileReaderTests
{
    [Fact]
    public async Task ReadAsync_accepts_large_typed_table_and_list_values_from_a_file()
    {
        var json = LargeTypedParametersJson();
        Assert.True(Encoding.UTF8.GetByteCount(json) > 64 * 1024);
        using var file = TempJson(json);
        var inline = JsonSerializer.Deserialize<InlineFixedParameterFile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var actual = await ParameterInputFileReader.ReadAsync(file.Path, CancellationToken.None);

        AssertTypedParametersEqual(inline.Parameters, actual);
        Assert.Equal(["tvp", "list"], actual.Select(parameter => parameter.Kind));
        Assert.Equal(2500, actual[0].Rows!.Count);
        Assert.Equal(5000, actual[1].Values!.Count);
        Assert.Equal(["00000000", "00000001", "00000002"], actual[0].Rows!.Take(3).Select(row => row[0]));
        Assert.Equal(["item-000000-abcdefghij", "item-000001-abcdefghij"], actual[1].Values!.Take(2));
    }

    [Fact]
    public async Task ReadAsync_accepts_exactly_16_mib()
    {
        using var exact = TempBytes(SizedJson(16 * 1024 * 1024));

        var parsed = await ParameterInputFileReader.ReadAsync(exact.Path, CancellationToken.None);
        Assert.Equal("1", Assert.Single(parsed).Value);
    }

    [Fact]
    public async Task ReadAsync_rejects_16_mib_plus_one()
    {
        using var over = TempBytes(SizedJson((16 * 1024 * 1024) + 1));

        var exception = await Assert.ThrowsAsync<ParameterSetFileException>(() =>
            ParameterInputFileReader.ReadAsync(over.Path, CancellationToken.None));
        Assert.Contains("16 MiB", exception.Message, StringComparison.Ordinal);
        Assert.Contains(over.Path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Budget_reads_growing_stream_with_a_sentinel_and_preserves_cancellation()
    {
        var budget = new ParameterFileIngressBudget();
        var exception = await Assert.ThrowsAsync<ParameterSetFileException>(() =>
            budget.ReadAsync(new GeneratedStream(int.MaxValue), "growing.sqljson", CancellationToken.None));
        Assert.Contains("16 MiB", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, budget.UsedBytes);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ParameterFileIngressBudget().ReadAsync(new GeneratedStream(int.MaxValue), "cancelled.sqljson", cts.Token));
    }

    [Fact]
    public async Task Budget_counts_repeated_occurrences_and_allows_exact_aggregate_boundary()
    {
        var budget = new ParameterFileIngressBudget();
        var exact = new GeneratedStream(ParameterFileIngressBudget.MaximumFileBytes);
        Assert.Equal(ParameterFileIngressBudget.MaximumFileBytes,
            (await budget.ReadAsync(exact, "same.sqljson", CancellationToken.None)).Length);
        Assert.Equal(ParameterFileIngressBudget.MaximumFileBytes,
            (await budget.ReadAsync(new GeneratedStream(ParameterFileIngressBudget.MaximumFileBytes), "same.sqljson", CancellationToken.None)).Length);
        Assert.Equal(ParameterFileIngressBudget.MaximumTotalBytes, budget.UsedBytes);

        var exception = await Assert.ThrowsAsync<ParameterSetFileException>(() =>
            budget.ReadAsync(new GeneratedStream(1), "third.sqljson", CancellationToken.None));
        Assert.Contains("32 MiB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parameter_set_reader_charges_external_bytes_once_including_typed_parser_wrappers()
    {
        const string json = "{\"name\":\"typed\",\"parameters\":[{\"name\":\"Ids\",\"type\":\"int\",\"kind\":\"list\",\"values\":[\"1\",\"2\"]}]}";
        using var file = TempJson(json);
        var budget = new ParameterFileIngressBudget();

        var set = await ParameterSetFileReader.ReadAsync(file.Path, budget, CancellationToken.None);

        Assert.Equal("typed", set.Name);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), budget.UsedBytes);
    }

    [Fact]
    public async Task Admitted_file_still_obeys_the_existing_combined_materialization_limit()
    {
        var values = Enumerable.Repeat("x", 100_001);
        using var file = TempJson(JsonSerializer.Serialize(new
        {
            parameters = new[] { new { name = "Ids", type = "int", kind = "list", values } },
        }));

        var parameters = await ParameterInputFileReader.ReadAsync(file.Path, CancellationToken.None);
        var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
            ParameterSetResolver.ValidateInputs(parameters, SqlEngine.SqlServer));

        Assert.Contains("100000", exception.Message, StringComparison.Ordinal);
    }

    private static string LargeTypedParametersJson()
    {
        var rows = Enumerable.Range(0, 2500).Select(index => new[] { $"{index:D8}", $"row-{index:D8}-abcdefghij" });
        var values = Enumerable.Range(0, 5000).Select(index => $"item-{index:D6}-abcdefghij");
        return JsonSerializer.Serialize(new
        {
            parameters = new object[]
            {
                new { name = "Rows", type = "dbo.IdRows", kind = "tvp", rows },
                new { name = "Ids", type = "int", kind = "list", values },
            },
        });
    }

    private static void AssertTypedParametersEqual(
        IReadOnlyList<SqlHarnessParameterInput> expected,
        IReadOnlyList<SqlHarnessParameterInput> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].Type, actual[index].Type);
            Assert.Equal(expected[index].Value, actual[index].Value);
            Assert.Equal(expected[index].Kind, actual[index].Kind);
            Assert.Equal(expected[index].SourceSql, actual[index].SourceSql);
            var expectedRows = expected[index].Rows;
            var actualRows = actual[index].Rows;
            Assert.Equal(expectedRows?.Count, actualRows?.Count);
            if (expectedRows is not null && actualRows is not null)
                for (var row = 0; row < expectedRows.Count; row++)
                    Assert.Equal(expectedRows[row], actualRows[row]);
            Assert.Equal(expected[index].Values, actual[index].Values);
        }
    }

    private sealed record InlineFixedParameterFile(SqlHarnessParameterInput[] Parameters);

    private static byte[] SizedJson(int byteCount)
    {
        const string head = "{\"parameters\":[{\"name\":\"id\",\"type\":\"int\",\"value\":\"1\"}]}";
        var bytes = new byte[byteCount];
        Encoding.UTF8.GetBytes(head).CopyTo(bytes, 0);
        bytes.AsSpan(head.Length, byteCount - head.Length).Fill((byte)' ');
        return bytes;
    }

    private static TempFile TempJson(string json) => TempBytes(Encoding.UTF8.GetBytes(json));

    private static TempFile TempBytes(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlharness-params-" + Guid.NewGuid().ToString("N") + ".sqljson");
        File.WriteAllBytes(path, bytes);
        return new TempFile(path);
    }

    private sealed class TempFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }

    private sealed class GeneratedStream(int remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, remaining);
            buffer.Span[..count].Fill((byte)' ');
            remaining -= count;
            return ValueTask.FromResult(count);
        }
    }
}