using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Integration;

public sealed class BenchmarkSessionIntegrationTests
{
    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Compare_setup_warmup_baseline_and_candidate_share_one_session_for_local_temp()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.DataSource));
        Assert.False(string.IsNullOrWhiteSpace(builder.InitialCatalog));

        using var temp = new TempDirectory();
        var factory = new IntegrationSessionFactory(connectionString);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["integration"] = new(
                builder.DataSource,
                builder.InitialCatalog,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "integrated"),
        };

        var module = new SqlHarnessModule(
            factory,
            new NullGainStore(),
            new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow),
            () => profiles);

        var operation = new SqlHarnessCompareOperation(
            new SqlTargetRequest("integration", new Dictionary<string, string>()),
            """
            CREATE TABLE #Req (Id int NOT NULL PRIMARY KEY);
            INSERT #Req VALUES (1);
            """,
            "SELECT Id FROM #Req;",
            "SELECT Id FROM #Req;",
            [],
            30,
            1);

        var outcome = await module.ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareReport>(outcome.Report);
        Assert.True(report.ResultsEquivalent);
        Assert.Equal(1, factory.ConnectCount);
    }

    private sealed class IntegrationSessionFactory(string connectionString) : ISqlSessionFactory
    {
        public int ConnectCount { get; private set; }

        public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectCount++;
            var connection = new SqlConnection(connectionString);
            var opened = false;
            try
            {
                var messages = new List<string>();
                SqlInfoMessageEventHandler handler = (_, args) => messages.Add(args.Message);
                connection.InfoMessage += handler;
                await connection.OpenAsync(ct);
                opened = true;
                var session = new SqlClientSession(connection, messages, handler)
                {
                    Identity = new SqlHarnessTargetIdentityReport(
                        connection.DataSource,
                        connection.Database,
                        connection.DataSource,
                        connection.Database,
                        target.Mode),
                };
                return session;
            }
            finally
            {
                if (!opened)
                    await connection.DisposeAsync();
            }
        }
    }

    private sealed class NullGainStore : IGainStore
    {
        public void Append(GainRecord record)
        {
        }

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sqlharness-integration-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of disposable artifact root.
            }
        }
    }
}
