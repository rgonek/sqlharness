using System.Diagnostics;
using System.Text;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class PlanCommandTests
{
    private const int MaximumPlanBytes = 16 * 1024 * 1024;
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan");

    [Fact]
    public async Task Plan_reads_file_and_renders_operator_tree()
    {
        var module = new DistillingModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["plan", Fixture]);

        Assert.Equal(0, exit);
        Assert.Contains("Index Seek", output.ToString(), StringComparison.Ordinal);
        Assert.Single(module.Operations);
    }

    [Fact]
    public async Task Plan_json_is_the_distilled_plan_model()
    {
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new DistillingModule(), output).RunAsync(["plan", Fixture, "--json"]);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.NotEmpty(json.RootElement.GetProperty("statements").EnumerateArray());
        Assert.Contains("Index Seek", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_missing_file_returns_safety_without_dispatch()
    {
        var module = new DistillingModule();

        var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["plan", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlplan")]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
    }

    [Fact]
    public async Task Plan_without_file_reads_stdin()
    {
        var module = new DistillingModule();
        var xml = await File.ReadAllTextAsync(Fixture);

        var exit = await SqlHarnessCli.Create(module, new StringWriter(), planStdin: new MemoryStream(Encoding.UTF8.GetBytes(xml))).RunAsync(["plan"]);

        Assert.Equal(0, exit);
        Assert.IsType<SqlHarnessPlanOperation>(Assert.Single(module.Operations));
    }

    [Fact]
    public async Task Plan_invalid_xml_returns_safety_and_records_actual_output_footprint()
    {
        var gain = new CapturingGainStore();
        var output = new StringWriter();
        var module = new SqlHarnessModule(new NeverSessionFactory(), gain, () => throw new InvalidOperationException("profiles must not load"));

        var exit = await SqlHarnessCli.Create(module, output, planStdin: new MemoryStream(Encoding.UTF8.GetBytes("<invalid />"))).RunAsync(["plan", "-"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        var record = Assert.Single(gain.Records);
        Assert.Equal("plan", record.Command);
        Assert.False(record.Success);
        Assert.Equal(Encoding.UTF8.GetByteCount("<invalid />"), record.RawBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(output.ToString()), record.EmittedBytes);
    }

    [Fact]
    public async Task Plan_gain_write_failure_returns_local_storage_after_rendering_valid_output()
    {
        var xml = await File.ReadAllTextAsync(Fixture);
        var output = new StringWriter();
        var module = new SqlHarnessModule(new NeverSessionFactory(), new ThrowingGainStore(), () => throw new InvalidOperationException("profiles must not load"));

        var exit = await SqlHarnessCli.Create(module, output, planStdin: new MemoryStream(Encoding.UTF8.GetBytes(xml))).RunAsync(["plan"]);

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("Index Seek", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_rejects_oversize_file_before_dispatch()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, new byte[MaximumPlanBytes + 1]);
        var module = new DistillingModule();
        try
        {
            var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(["plan", path]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Empty(module.Operations);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Plan_stdin_stops_after_limit_detection_byte()
    {
        var stream = new EndlessStream();
        var module = new DistillingModule();

        var exit = await SqlHarnessCli.Create(module, new StringWriter(), planStdin: stream).RunAsync(["plan"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.Equal(MaximumPlanBytes + 1, stream.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_BOM_inputs_preserve_actual_raw_byte_count(bool utf16)
    {
        var xml = await File.ReadAllTextAsync(Fixture);
        Encoding encoding = utf16 ? new UnicodeEncoding(false, true, true) : new UTF8Encoding(true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray();
        var gain = new CapturingGainStore();
        var module = new SqlHarnessModule(new NeverSessionFactory(), gain, () => throw new InvalidOperationException());

        var exit = await SqlHarnessCli.Create(module, new StringWriter(), planStdin: new MemoryStream(bytes)).RunAsync(["plan"]);

        Assert.Equal(0, exit);
        Assert.Equal(bytes.Length, Assert.Single(gain.Records).RawBytes);
    }

    [Fact]
    public async Task Plan_malformed_encoding_is_sanitized_safety_failure()
    {
        var output = new StringWriter();
        var module = new DistillingModule();

        var exit = await SqlHarnessCli.Create(module, output, planStdin: new MemoryStream([0xC3, 0x28])).RunAsync(["plan"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.DoesNotContain("C3", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plan_process_reads_standard_input_bytes()
    {
        var home = Path.Combine(Path.GetTempPath(), "sqlharness-plan-process-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet", $"\"{typeof(SqlHarnessCli).Assembly.Location}\" plan - --json")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["SQLHARNESS_HOME"] = home;
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(await File.ReadAllTextAsync(Fixture));
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        try
        {
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
            using var json = JsonDocument.Parse(stdout);
            Assert.NotEmpty(json.RootElement.GetProperty("statements").EnumerateArray());
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }

    private sealed class DistillingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            var plan = Assert.IsType<SqlHarnessPlanOperation>(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, PlanDistiller.Distill(plan.ShowplanXml), null));
        }
    }

    private sealed class NeverSessionFactory : ISqlSessionFactory
    {
        public Task<ISqlSession> ConnectAsync(SqlHarness.Core.Targets.ResolvedTarget target, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("plan command must not connect to a database");
    }

    private sealed class CapturingGainStore : IGainStore
    {
        public List<GainRecord> Records { get; } = [];
        public void Append(GainRecord record) => Records.Add(record);
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class ThrowingGainStore : IGainStore
    {
        public void Append(GainRecord record) => throw new IOException("gain unavailable");
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'x', offset, count); BytesRead += count; return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { buffer.Span.Fill((byte)'x'); BytesRead += buffer.Length; return ValueTask.FromResult(buffer.Length); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}