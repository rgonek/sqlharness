using System.Text;
using System.Text.Json;

using SqlHarness.Cli.Infrastructure;

namespace SqlHarness.Tests;

public sealed class ParameterSetFileReaderTests
{
    private const string ParameterSecret = "customerId:int=424242";
    private const string InvalidFileMessage = "The parameter set file is invalid.";
    private const string TooLargeMessage = "Parameter set file exceeds the 64 KiB limit.";
    private const string UnableToReadMessage = "Unable to read parameter set file.";

    [Fact]
    public async Task ReadAsync_returns_name_and_parameters_in_file_order()
    {
        using var file = TempJson("""{"name":"large-customer","parameters":["customerId:int=42","asOf:date=2026-06-30"]}""");

        var set = await ParameterSetFileReader.ReadAsync(file.Path, CancellationToken.None);

        Assert.Equal("large-customer", set.Name);
        Assert.Equal(["customerId:int=42", "asOf:date=2026-06-30"], set.Parameters);
    }

    [Fact]
    public async Task ReadAsync_accepts_parameters_before_name_and_trailing_whitespace()
    {
        using var file = TempJson("""  {"parameters":["id:int=1","asOf:date=2026-06-30"],"name":"large-customer"}  """ + "\n\t");

        var set = await ParameterSetFileReader.ReadAsync(file.Path, CancellationToken.None);

        Assert.Equal("large-customer", set.Name);
        Assert.Equal(["id:int=1", "asOf:date=2026-06-30"], set.Parameters);
    }

    [Fact]
    public async Task ReadAsync_preserves_utf8_parameter_text_and_comment_like_strings()
    {
        using var file = TempJson("""{"name":"cafe","parameters":["id:nvarchar=café","note:nvarchar=a /* secret-note-value */ b // c"]}""");

        var set = await ParameterSetFileReader.ReadAsync(file.Path, CancellationToken.None);

        Assert.Equal("cafe", set.Name);
        Assert.Equal(
            ["id:nvarchar=café", "note:nvarchar=a /* secret-note-value */ b // c"],
            set.Parameters);
    }

    [Fact]
    public async Task ReadAsync_accepts_exact_64_kib()
    {
        var secret = "id:int=424242-size-secret";
        using var file = TempBytes(SizedJson(64 * 1024, secret));

        var set = await ParameterSetFileReader.ReadAsync(file.Path, CancellationToken.None);

        Assert.Equal("boundary", set.Name);
        Assert.Equal([secret], set.Parameters);
    }

    [Fact]
    public async Task ReadAsync_rejects_one_byte_over_64_kib_without_parameter_text()
    {
        var secret = "id:int=424242-size-secret";
        using var file = TempBytes(SizedJson((64 * 1024) + 1, secret));

        var exception = await RejectBytes(file, TooLargeMessage);

        AssertSafe(exception, file.Path, secret, "424242-size-secret", "boundary");
    }

    [Fact]
    public async Task ReadAsync_rejects_larger_invalid_bytes_as_too_big()
    {
        var bytes = new byte[(64 * 1024) + 4096];
        bytes.AsSpan().Fill(0xFF);
        using var file = TempBytes(bytes);

        await RejectBytes(file, TooLargeMessage);
    }

    [Fact]
    public async Task ReadAsync_rejects_bom()
    {
        var json = """{"name":"ok","parameters":["customerId:int=424242"]}"""u8.ToArray();
        var bytes = new byte[Encoding.UTF8.Preamble.Length + json.Length];
        Encoding.UTF8.Preamble.CopyTo(bytes);
        json.CopyTo(bytes.AsSpan(Encoding.UTF8.Preamble.Length));
        using var file = TempBytes(bytes);

        var exception = await RejectBytes(file, InvalidFileMessage);

        AssertSafe(exception, file.Path, ParameterSecret, "ok");
    }

    [Theory]
    [InlineData("""{"name":"ok" /* secret-note-value */,"parameters":["customerId:int=424242"]}""")]
    [InlineData("""{"name":"ok","parameters":["customerId:int=424242"]} // secret-note-value""")]
    [InlineData("""{/* secret-note-value */"name":"ok","parameters":["customerId:int=424242"]}""")]
    public async Task ReadAsync_rejects_comments(string json)
    {
        using var file = TempJson(json);
        var exception = await Reject(file, InvalidFileMessage);
        AssertSafe(exception, file.Path, ParameterSecret, "secret-note-value");
    }

    [Theory]
    [InlineData("""{"name":"ok","parameters":["customerId:int=424242",]}""")]
    [InlineData("""{"name":"ok","parameters":["customerId:int=424242"],}""")]
    [InlineData("""{"name":"ok","parameters":["customerId:int=424242",],}""")]
    public async Task ReadAsync_rejects_trailing_commas(string json)
    {
        using var file = TempJson(json);
        var exception = await Reject(file, InvalidFileMessage);
        AssertSafe(exception, file.Path, ParameterSecret);
    }

    [Theory]
    [InlineData("""{"name":"ok","parameters":["customerId:int=424242"]}{"name":"other","parameters":["id:int=1"]}""")]
    [InlineData("""{"name":"ok","parameters":["customerId:int=424242"]} true""")]
    [InlineData("{\"name\":\"ok\",\"parameters\":[\"customerId:int=424242\"]}\n[]")]
    public async Task ReadAsync_rejects_trailing_content(string json)
    {
        using var file = TempJson(json);
        var exception = await Reject(file, InvalidFileMessage);
        AssertSafe(exception, file.Path, ParameterSecret, "other");
    }

    [Theory]
    [InlineData("""{"name":"first-secret","name":"second-secret","parameters":["customerId:int=424242"]}""")]
    [InlineData("""{"name":"ok","parameters":["customerId:int=1"],"parameters":["customerId:int=424242"]}""")]
    public async Task ReadAsync_rejects_duplicate_properties(string json)
    {
        using var file = TempJson(json);
        var exception = await Reject(file, InvalidFileMessage);
        AssertSafe(exception, file.Path, ParameterSecret, "first-secret", "second-secret");
    }

    [Fact]
    public async Task ReadAsync_rejects_duplicate_json_names()
    {
        using var file = TempJson("""{"name":"alpha-secret","\u006eame":"beta-secret","parameters":["customerId:int=424242"]}""");

        var exception = await Reject(file, InvalidFileMessage);

        AssertSafe(exception, file.Path, ParameterSecret, "alpha-secret", "beta-secret");
    }

    [Theory]
    [InlineData("""{"label":"secret-label","parameters":["customerId:int=424242"]}""")]
    [InlineData("""{"Name":"secret-label","parameters":["customerId:int=424242"]}""")]
    [InlineData("""{"name":"ok","Parameters":["customerId:int=424242"]}""")]
    public async Task ReadAsync_rejects_unknown_properties(string json)
    {
        using var file = TempJson(json);
        var exception = await Reject(file, InvalidFileMessage);
        AssertSafe(exception, file.Path, ParameterSecret, "secret-label");
    }

    [Fact]
    public async Task ReadAsync_rejects_extra_fields()
    {
        using var file = TempJson("""{"name":"ok","parameters":["customerId:int=424242"],"note":"secret-note-value","path":"D:\\secret\\one.sqljson"}""");

        var exception = await Reject(file, InvalidFileMessage);

        AssertSafe(exception, file.Path, ParameterSecret, "secret-note-value", "one.sqljson", "D:\\secret\\one.sqljson");
    }

    [Theory]
    [InlineData("""{"parameters":["customerId:int=424242"]}""")]
    [InlineData("""{"name":"secret-label"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"name":"secret-label","parameters":null}""")]
    [InlineData("""{"name":null,"parameters":["customerId:int=424242"]}""")]
    public async Task ReadAsync_rejects_missing_fields(string json)
    {
        using var file = TempJson(json);
        var exception = await Reject(file, InvalidFileMessage);
        AssertSafe(exception, file.Path, ParameterSecret, "secret-label");
    }

    [Theory]
    [MemberData(nameof(InvalidLabels))]
    public async Task ReadAsync_rejects_invalid_labels(string label)
    {
        var json = JsonSerializer.Serialize(new { name = label, parameters = new[] { ParameterSecret } });
        using var file = TempJson(json);

        var exception = await Reject(file, InvalidFileMessage);

        AssertSafe(exception, file.Path, ParameterSecret, label);
    }

    [Theory]
    [MemberData(nameof(ValidLabels))]
    public async Task ReadAsync_accepts_valid_labels(string label)
    {
        var json = JsonSerializer.Serialize(new { name = label, parameters = new[] { "id:int=1" } });
        using var file = TempJson(json);

        var set = await ParameterSetFileReader.ReadAsync(file.Path, CancellationToken.None);

        Assert.Equal(label, set.Name);
        Assert.Equal(["id:int=1"], set.Parameters);
    }

    [Theory]
    [InlineData("""{"name":"ok","parameters":[]}""")]
    [InlineData("""{"name":"ok","parameters":[""]}""")]
    [InlineData("""{"name":"ok","parameters":["customerId:int=424242",""]}""")]
    public async Task ReadAsync_rejects_empty_parameters(string json)
    {
        using var file = TempJson(json);
        var exception = await Reject(file, InvalidFileMessage);
        AssertSafe(exception, file.Path, ParameterSecret);
    }

    [Theory]
    [InlineData("""{"name":1,"parameters":["customerId:int=424242"]}""")]
    [InlineData("""{"name":"ok","parameters":"customerId:int=424242"}""")]
    [InlineData("""{"name":"ok","parameters":[{"id":"424242"}]}""")]
    [InlineData("""{"name":"ok","parameters":[null]}""")]
    [InlineData("""["secret-label"]""")]
    [InlineData("""null""")]
    [InlineData("customerId:int=424242")]
    public async Task ReadAsync_rejects_wrong_json_shape(string json)
    {
        using var file = TempJson(json);
        var exception = await Reject(file, InvalidFileMessage);
        AssertSafe(exception, file.Path, ParameterSecret, "424242", "secret-label");
    }

    [Fact]
    public async Task ReadAsync_rejects_invalid_utf8()
    {
        var json = """{"name":"ok","parameters":["customerId:int=424242"]}"""u8.ToArray();
        var bytes = new byte[json.Length + 1];
        Array.Copy(json, bytes, json.Length);
        bytes[^1] = 0xFF;
        using var file = TempBytes(bytes);

        var exception = await RejectBytes(file, InvalidFileMessage);

        AssertSafe(exception, file.Path, ParameterSecret);
    }

    [Fact]
    public async Task ReadAsync_rejects_missing_file_without_the_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlharness-missing-" + Guid.NewGuid().ToString("n") + ".sqljson");

        var exception = await Assert.ThrowsAsync<ParameterSetFileException>(() =>
            ParameterSetFileReader.ReadAsync(path, CancellationToken.None));

        Assert.Equal(UnableToReadMessage, exception.Message);
        AssertSafe(exception, path);
    }

    [Fact]
    public async Task ReadAsync_rejects_directory_without_the_path()
    {
        var directory = Directory.CreateTempSubdirectory("sqlharness-ps-dir");
        try
        {
            var exception = await Assert.ThrowsAsync<ParameterSetFileException>(() =>
                ParameterSetFileReader.ReadAsync(directory.FullName, CancellationToken.None));

            Assert.Equal(UnableToReadMessage, exception.Message);
            AssertSafe(exception, directory.FullName, directory.Name);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_rejects_illegal_path_without_echoing_it()
    {
        var path = Path.Combine(Path.GetTempPath(), "bad|name-" + Guid.NewGuid().ToString("n") + ".sqljson");

        var exception = await Assert.ThrowsAsync<ParameterSetFileException>(() =>
            ParameterSetFileReader.ReadAsync(path, CancellationToken.None));

        Assert.Equal(UnableToReadMessage, exception.Message);
        AssertSafe(exception, path, "bad|name");
    }

    [Fact]
    public async Task ReadAsync_cancellation_is_not_a_parameter_set_error()
    {
        using var file = TempJson("""{"name":"ok","parameters":["id:int=1"]}""");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ParameterSetFileReader.ReadAsync(file.Path, cts.Token));
    }

    public static TheoryData<string> InvalidLabels()
    {
        var labels = new TheoryData<string>
        {
            "",
            " ",
            ".hidden",
            "-dash",
            "_underscore",
            "has space",
            "slash/name",
            """slash\name""",
            "bang!",
            "café",
            new string('a', 65),
            "@name",
            "name ",
            " name",
            "a\n",
            "a\t",
        };
        return labels;
    }

    public static TheoryData<string> ValidLabels()
    {
        var labels = new TheoryData<string>
        {
            "a",
            "Z",
            "0",
            "a.b_c-d",
            "A1",
            "9name",
            new string('b', 64),
        };
        return labels;
    }

    private static async Task<ParameterSetFileException> Reject(TempFile file, string message)
    {
        var exception = await Assert.ThrowsAsync<ParameterSetFileException>(() =>
            ParameterSetFileReader.ReadAsync(file.Path, CancellationToken.None));
        Assert.Equal(message, exception.Message);
        return exception;
    }

    private static Task<ParameterSetFileException> RejectBytes(TempFile file, string message) =>
        Reject(file, message);

    private static void AssertSafe(Exception exception, params string?[] forbidden)
    {
        Assert.Null(exception.InnerException);
        foreach (var value in forbidden)
        {
            // A one-character or whitespace label is already covered by the fixed message equality.
            if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
                continue;
            Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
        }
    }

    private static byte[] SizedJson(int byteCount, string parameter)
    {
        var head = Encoding.UTF8.GetBytes($$"""{"name":"boundary","parameters":["{{parameter}}"]""");
        var tail = "}"u8;
        var bytes = new byte[byteCount];
        Array.Copy(head, bytes, head.Length);
        bytes.AsSpan(head.Length, byteCount - head.Length - tail.Length).Fill((byte)' ');
        tail.CopyTo(bytes.AsSpan(byteCount - tail.Length));
        return bytes;
    }

    private static TempFile TempJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlharness-ps-" + Guid.NewGuid().ToString("n") + ".sqljson");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new TempFile(path);
    }

    private static TempFile TempBytes(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlharness-ps-" + Guid.NewGuid().ToString("n") + ".sqljson");
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
