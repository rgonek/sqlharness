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

        var actual = await ParameterInputFileReader.ReadAsync(file.Path, CancellationToken.None);

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
}
