using System.Text.Json;
using System.Text;
using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class PlanCommandTests
{
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

        var exit = await SqlHarnessCli.Create(module, new StringWriter(), new StringReader(xml), true).RunAsync(["plan"]);

        Assert.Equal(0, exit);
        Assert.IsType<SqlHarnessPlanOperation>(Assert.Single(module.Operations));
    }

    [Fact]
    public async Task Plan_invalid_xml_returns_safety_and_records_actual_output_footprint()
    {
        var gain = new CapturingGainStore();
        var output = new StringWriter();
        var module = new SqlHarnessModule(new NeverSessionFactory(), gain, () => throw new InvalidOperationException("profiles must not load"));

        var exit = await SqlHarnessCli.Create(module, output, new StringReader("<invalid />"), true).RunAsync(["plan", "-"]);

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

        var exit = await SqlHarnessCli.Create(module, output, new StringReader(xml), true).RunAsync(["plan"]);

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("Index Seek", output.ToString(), StringComparison.Ordinal);
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
}
