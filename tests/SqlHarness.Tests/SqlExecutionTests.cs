using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class SqlExecutionTests
{
    [Theory]
    [InlineData("ad-default")]
    [InlineData("integrated")]
    [InlineData("sql")]
    public async Task Non_azure_cli_auth_never_requests_an_access_token(string authName)
    {
        const string passwordVariable = "SQLHARNESS_EXECUTION_TEST_PASSWORD";
        Environment.SetEnvironmentVariable(passwordVariable, "top-secret");
        try
        {
            var cli = new FakeAzureCli("unused");
            var connector = new FakeConnector("server", "database");
            var auth = AuthSpec.Parse(authName, "user", passwordVariable, false);
            var factory = new SqlClientSessionFactory(cli, connector.ConnectAsync);

            await using var session = await factory.ConnectAsync(
                new ResolvedTarget("server", "database", auth, "direct"), default);

            Assert.Equal(0, cli.RunCount);
            Assert.Null(connector.AccessToken);
            Assert.Equal(SqlExecution.IdentitySql, connector.Session.Commands.Single().Sql);
            Assert.Equal("direct", session.Identity.Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Fact]
    public async Task Azure_cli_auth_requests_SQL_token_and_assigns_it_to_connection()
    {
        var cli = new FakeAzureCli("azure-token");
        var connector = new FakeConnector("logical.database.windows.net", "database");
        var factory = new SqlClientSessionFactory(cli, connector.ConnectAsync);

        await using var session = await factory.ConnectAsync(
            new ResolvedTarget("logical", "database", AuthSpec.Parse("azure-cli", null, null, false), "profile"),
            default);

        Assert.Equal(1, cli.RunCount);
        Assert.Equal(
            ["account", "get-access-token", "--resource", "https://database.windows.net/"],
            cli.LastArgs);
        Assert.Equal("azure-token", connector.AccessToken);
        Assert.Equal("profile", session.Identity.Mode);
    }

    [Fact]
    public async Task Connection_uses_the_resolved_auth_spec()
    {
        const string variable = "SQLHARNESS_EXECUTION_CONNECTION_PASSWORD";
        Environment.SetEnvironmentVariable(variable, "connection-secret");
        try
        {
            var connector = new FakeConnector("server", "database");
            var factory = new SqlClientSessionFactory(new FakeAzureCli("unused"), connector.ConnectAsync, 12);
            var auth = AuthSpec.Parse("sql", "app-user", variable, true);

            await using var session = await factory.ConnectAsync(
                new ResolvedTarget("server", "database", auth, "direct"), default);

            var builder = new SqlConnectionStringBuilder(connector.ConnectionString);
            Assert.Equal("server", builder.DataSource);
            Assert.Equal("database", builder.InitialCatalog);
            Assert.Equal("app-user", builder.UserID);
            Assert.Equal("connection-secret", builder.Password);
            Assert.True(builder.TrustServerCertificate);
            Assert.Equal(12, builder.ConnectTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Direct_mode_rejects_a_post_connect_identity_mismatch()
    {
        var connector = new FakeConnector("other-server", "database");
        var factory = new SqlClientSessionFactory(new FakeAzureCli("unused"), connector.ConnectAsync);

        var error = await Assert.ThrowsAsync<SqlTargetMismatchException>(() => factory.ConnectAsync(
            new ResolvedTarget("server", "database", AuthSpec.Parse("integrated", null, null, false), "direct"),
            default));

        Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(connector.Session.Disposed);
    }

    [Theory]
    [InlineData("localhost,14334")]
    [InlineData("127.0.0.1,14334")]
    [InlineData("tcp:localhost,14334")]
    [InlineData("::1,14334")]
    [InlineData("[::1],14334")]
    [InlineData(".")]
    [InlineData("(local)")]
    public async Task Loopback_endpoint_accepts_container_or_machine_server_name(string dataSource)
    {
        // Docker SQL reports a container hostname via SERVERPROPERTY('ServerName'), not localhost.
        var connector = new FakeConnector("9134b03e1a34", "CivicLens");
        var factory = new SqlClientSessionFactory(new FakeAzureCli("unused"), connector.ConnectAsync);
        const string passwordVariable = "SQLHARNESS_LOOPBACK_IDENTITY_PASSWORD";
        Environment.SetEnvironmentVariable(passwordVariable, "secret");
        try
        {
            var auth = AuthSpec.Parse("sql", "sa", passwordVariable, true);
            await using var session = await factory.ConnectAsync(
                new ResolvedTarget(dataSource, "CivicLens", auth, "profile"), default);

            Assert.Equal(dataSource, session.Identity.RequestedServer);
            Assert.Equal("CivicLens", session.Identity.ActualDatabase);
            Assert.Equal("9134b03e1a34", session.Identity.ActualServer);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Fact]
    public async Task Loopback_endpoint_still_rejects_database_mismatch()
    {
        var connector = new FakeConnector("9134b03e1a34", "OtherDb");
        var factory = new SqlClientSessionFactory(new FakeAzureCli("unused"), connector.ConnectAsync);
        const string passwordVariable = "SQLHARNESS_LOOPBACK_DB_MISMATCH_PASSWORD";
        Environment.SetEnvironmentVariable(passwordVariable, "secret");
        try
        {
            var auth = AuthSpec.Parse("sql", "sa", passwordVariable, true);
            var error = await Assert.ThrowsAsync<SqlTargetMismatchException>(() => factory.ConnectAsync(
                new ResolvedTarget("localhost,14334", "CivicLens", auth, "profile"), default));

            Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(connector.Session.Disposed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Fact]
    public async Task Remote_server_with_client_port_matches_machine_server_name()
    {
        // On-prem targets often use host,port while SERVERPROPERTY returns the host only.
        var connector = new FakeConnector("sql-box", "AppDb");
        var factory = new SqlClientSessionFactory(new FakeAzureCli("unused"), connector.ConnectAsync);

        await using var session = await factory.ConnectAsync(
            new ResolvedTarget("sql-box,1433", "AppDb", AuthSpec.Parse("integrated", null, null, false), "direct"),
            default);

        Assert.Equal("sql-box,1433", session.Identity.RequestedServer);
        Assert.Equal("sql-box", session.Identity.ActualServer);
    }

    [Theory]
    [InlineData("localhost,14334", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("tcp:localhost,1433", true)]
    [InlineData("sql-box,1433", false)]
    [InlineData("logical.database.windows.net", false)]
    public void IsLoopbackEndpoint_classifies_data_sources(string dataSource, bool expected) =>
        Assert.Equal(expected, SqlClientSessionFactory.IsLoopbackEndpoint(dataSource));

    [Fact]
    public void Command_binds_parsed_parameters_without_interpolating_values()
    {
        const string sql = "SELECT * FROM dbo.Client WHERE Name = @name AND Id = @id";
        var parsed = SqlParameterParser.Parse(["name=Robert'); DROP TABLE dbo.Client;--", "id:int=42"]);
        using var command = new SqlCommand();

        SqlClientSession.BindCommand(command, new SqlExecutionCommand(sql, parsed, 30));

        Assert.Equal(sql, command.CommandText);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal("Robert'); DROP TABLE dbo.Client;--", command.Parameters["@name"].Value);
        Assert.Equal(42, command.Parameters["@id"].Value);
    }

    [Fact]
    public async Task Reader_disposal_always_disposes_command_when_reader_disposal_throws()
    {
        using var command = new SqlCommand();
        var commandDisposed = false;
        command.Disposed += (_, _) => commandDisposed = true;
        var invalidReader = (SqlDataReader)RuntimeHelpers.GetUninitializedObject(typeof(SqlDataReader));
        var wrapper = new SqlClientReader(command, invalidReader);

        await Assert.ThrowsAnyAsync<Exception>(async () => await wrapper.DisposeAsync());

        Assert.True(commandDisposed);
    }

    private sealed class FakeAzureCli(string token) : IAzureCli
    {
        public int RunCount { get; private set; }
        public IReadOnlyList<string>? LastArgs { get; private set; }
        public Task<bool> IsLoggedInAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<JsonElement> RunJsonAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            RunCount++;
            LastArgs = args;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { accessToken = token }));
        }
    }

    private sealed class FakeConnector(string actualServer, string actualDatabase)
    {
        public FakeSession Session { get; } = new(actualServer, actualDatabase);
        public string? ConnectionString { get; private set; }
        public string? AccessToken { get; private set; }

        public Task<ISqlSession> ConnectAsync(string connectionString, string? accessToken, CancellationToken ct)
        {
            ConnectionString = connectionString;
            AccessToken = accessToken;
            return Task.FromResult<ISqlSession>(Session);
        }
    }

    private sealed class FakeSession(string server, string database) : ISqlSession
    {
        public List<SqlExecutionCommand> Commands { get; } = [];
        public bool Disposed { get; private set; }
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult<ISqlReader>(new FakeReader(server, database));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeReader(string server, string database) : ISqlReader
    {
        private bool _read;
        public int FieldCount => 2;
        public int RecordsAffected => 0;
        public string GetName(int ordinal) => ordinal == 0 ? "ServerName" : "DatabaseName";
        public Type GetFieldType(int ordinal) => typeof(string);
        public bool GetAllowNull(int ordinal) => false;
        public object GetValue(int ordinal) => ordinal == 0 ? server : database;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(!_read && (_read = true));
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}