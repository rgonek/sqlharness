using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class ArtifactStorageOperationTests
{
    [Theory]
    [InlineData("compare")]
    [InlineData("matrix")]
    [InlineData("measure")]
    [InlineData("sets")]
    public async Task Unavailable_storage_fails_before_opening_a_sql_session(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlharness-preflight-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "existing file must survive");
        try
        {
            var factory = new RejectingSessionFactory();
            var module = new SqlHarnessModule(factory, new UnusedGainSource(),
                new CompareArtifactWriter(root, () => DateTimeOffset.UtcNow), Profiles);
            var outcome = await module.ExecuteAsync(Operation(mode));

            Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
            Assert.Equal(0, factory.OpenCount);
            Assert.Null(outcome.Report);
            var error = Assert.IsType<SqlHarnessError>(outcome.MachineError);
            Assert.Equal("local_storage_failed", error.Code);
            Assert.Equal("artifact-preflight", error.Phase);
            Assert.Contains("process account", error.Hint);
            Assert.Equal(root, error.Location?.Path);
            Assert.Equal("existing file must survive", File.ReadAllText(root));
        }
        finally
        {
            File.Delete(root);
        }
    }

    [Theory]
    [InlineData("compare")]
    [InlineData("matrix")]
    [InlineData("measure")]
    [InlineData("sets")]
    public async Task Unsafe_sql_is_rejected_before_storage_is_probed(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlharness-invalid-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "existing file");
        try
        {
            var factory = new RejectingSessionFactory();
            var module = new SqlHarnessModule(factory, new UnusedGainSource(),
                new CompareArtifactWriter(root, () => DateTimeOffset.UtcNow), Profiles);
            SqlHarnessOperation operation = Operation(mode) switch
            {
                SqlHarnessCompareOperation compare => compare with { BaselineSql = "DELETE FROM dbo.Clients" },
                SqlHarnessCompareMatrixOperation matrix => matrix with { BaselineSql = "DELETE FROM dbo.Clients WHERE Id = @Value" },
                SqlHarnessMeasureOperation measure => measure with { QuerySql = "DELETE FROM dbo.Clients" },
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };

            var outcome = await module.ExecuteAsync(operation);

            Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
            Assert.Equal(0, factory.OpenCount);
            Assert.Equal("safety_rejected", outcome.MachineError?.Code);
            Assert.Equal("existing file", File.ReadAllText(root));
        }
        finally
        {
            File.Delete(root);
        }
    }

    private static SqlHarnessOperation Operation(string mode)
    {
        var target = new SqlTargetRequest("test", new Dictionary<string, string>());
        return mode switch
        {
            "compare" => new SqlHarnessCompareOperation(target, null, "SELECT 1", "SELECT 1", [], 30, 1),
            "matrix" => new SqlHarnessCompareMatrixOperation(target, null, "SELECT @Value", "SELECT @Value", [], 30, 1, "Value:int=1000000001,1000000002"),
            "measure" => new SqlHarnessMeasureOperation(target, null, "SELECT 1", [], 30, 1),
            "sets" => new SqlHarnessMeasureOperation(target, null, "SELECT @Value", [], 30, 1,
                [new("first", ["Value:int=1000000001"]), new("second", ["Value:int=1000000002"])]),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile> { ["test"] = new("test-server", "testdb", new Dictionary<string, string>(), "integrated") };

    private sealed class UnusedGainSource : IGainSource
    {
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class RejectingSessionFactory : ISqlSessionFactory
    {
        public int OpenCount { get; private set; }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            OpenCount++;
            throw new InvalidOperationException("SQL must not start when artifact storage is unavailable.");
        }
    }
}