using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class CompareContextCommandTests
{
    [Fact]
    public async Task Missing_context_is_rejected_before_sql_files_are_read_or_module_is_called()
    {
        var module = new RecordingModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output).RunAsync(
            ["compare", "dev", "--baseline", "missing-baseline.sql", "--candidate", "missing-candidate.sql"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Equal(0, module.CallCount);
        Assert.Contains("--subject", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Context_flows_to_plain_and_matrix_compare(bool matrix)
    {
        var baseline = TempFile("SELECT 1");
        var candidate = TempFile("SELECT 2");
        try
        {
            var module = new RecordingModule();
            var args = new List<string>
            {
                "compare", "dev", "--baseline", baseline, "--candidate", candidate,
                "--subject", "orders-report", "--scope", "whole",
            };
            if (matrix)
                args.AddRange(["--matrix", "N:int=1,2"]);

            var exit = await SqlHarnessCli.Create(module, new StringWriter()).RunAsync(args);

            Assert.Equal(0, exit);
            Assert.Equal(1, module.CallCount);
            var context = module.Operation switch
            {
                SqlHarnessCompareOperation compare => compare.Context,
                SqlHarnessCompareMatrixOperation compare => compare.Context,
                _ => null,
            };
            Assert.Equal(new CompareContext("orders-report", "whole"), context);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(candidate);
        }
    }

    [Fact]
    public async Task Disabled_context_accepts_omission_and_rejects_a_partial_pair()
    {
        var baseline = TempFile("SELECT 1");
        var candidate = TempFile("SELECT 2");
        try
        {
            var module = new RecordingModule();
            var config = new CompareConfig { RequireContext = false };
            var accepted = await SqlHarnessCli.Create(module, new StringWriter(), compareConfig: config).RunAsync(
                ["compare", "dev", "--baseline", baseline, "--candidate", candidate]);
            Assert.Equal(0, accepted);
            Assert.Null(module.Operation is SqlHarnessCompareOperation plain ? plain.Context :
                ((SqlHarnessCompareMatrixOperation)module.Operation!).Context);

            var output = new StringWriter();
            var rejected = await SqlHarnessCli.Create(module, output, compareConfig: config).RunAsync(
                ["compare", "dev", "--baseline", baseline, "--candidate", candidate, "--subject", "orders-report"]);
            Assert.Equal((int)SqlHarnessExitCode.Safety, rejected);
            Assert.Equal(1, module.CallCount);
            Assert.Contains("--subject and --scope", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(candidate);
        }
    }

    private static string TempFile(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlharness-context-" + Guid.NewGuid().ToString("N") + ".sql");
        File.WriteAllText(path, text);
        return path;
    }

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public int CallCount { get; private set; }
        public SqlHarnessOperation? Operation { get; private set; }

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            CallCount++;
            Operation = operation;
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }
}