using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Data.SqlClient;

using Npgsql;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;
using SqlHarness.Tests.Postgres;

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
    public void Command_binds_decimal_precision_and_scale_metadata()
    {
        using var typedCommand = new SqlCommand();
        SqlClientSession.BindCommand(
            typedCommand,
            new SqlExecutionCommand(
                "SELECT @amount",
                SqlParameterParser.Parse(["amount:decimal(19,4)=1234.5600"]),
                30));

        Assert.Equal((byte)19, typedCommand.Parameters["@amount"].Precision);
        Assert.Equal((byte)4, typedCommand.Parameters["@amount"].Scale);

        using var plainCommand = new SqlCommand();
        SqlClientSession.BindCommand(
            plainCommand,
            new SqlExecutionCommand(
                "SELECT @amount",
                SqlParameterParser.Parse(["amount:decimal=1234.56"]),
                30));

        using var baseline = new SqlCommand();
        var baselineParameter = baseline.Parameters.Add("@amount", System.Data.SqlDbType.Decimal);
        baselineParameter.Value = 1234.56m;

        Assert.Equal(baselineParameter.Precision, plainCommand.Parameters["@amount"].Precision);
        Assert.Equal(baselineParameter.Scale, plainCommand.Parameters["@amount"].Scale);
    }

    [Fact]
    public void Command_binds_nvarchar_max_and_typed_null_metadata()
    {
        var longText = new string('z', 5000);
        using var command = new SqlCommand();
        SqlClientSession.BindCommand(
            command,
            new SqlExecutionCommand(
                "SELECT @note, @count",
                SqlParameterParser.Parse([$"note:nvarchar(max)={longText}", "count:int:null"]),
                30));

        Assert.Equal(-1, command.Parameters["@note"].Size);
        Assert.Equal(longText, command.Parameters["@note"].Value);
        Assert.Equal(System.Data.SqlDbType.Int, command.Parameters["@count"].SqlDbType);
        Assert.Equal(DBNull.Value, command.Parameters["@count"].Value);
    }

    [Fact]
    public void Command_binds_hierarchyid_and_geography_as_udt()
    {
        using var command = new SqlCommand();
        SqlClientSession.BindCommand(
            command,
            new SqlExecutionCommand(
                "SELECT @path, @loc",
                SqlParameterParser.Parse([
                    "path:hierarchyid=/1/2/",
                    "loc:geography=4326;POINT(-122.34900 47.65100)"]),
                30));

        Assert.Equal(System.Data.SqlDbType.Udt, command.Parameters["@path"].SqlDbType);
        Assert.Equal("HierarchyId", command.Parameters["@path"].UdtTypeName);
        Assert.Equal(System.Data.SqlDbType.Udt, command.Parameters["@loc"].SqlDbType);
        Assert.Equal("Geography", command.Parameters["@loc"].UdtTypeName);
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

    [Fact]
    public void Verified_dns_host_matches_the_connected_address()
    {
        var resolver = new ScriptedResolver(IPAddress.Parse("192.0.2.1"));
        var target = PostgresTarget("db.example.test", false, new PostgresTransport(PostgresSslMode.VerifyFull, null));
        var observation = new PostgresEndpointObservation(IPAddress.Parse("192.0.2.1"), ["db.example.test"]);

        Assert.True(PostgresEndpointIdentity.Matches(target, "appdb", observation, resolver));
        Assert.Equal("db.example.test", resolver.Host);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void Database_mismatch_fails_without_calling_the_resolver()
    {
        var resolver = new ScriptedResolver(IPAddress.Parse("192.0.2.1")) { Throw = true };
        var target = PostgresTarget("db.example.test", false, new PostgresTransport(PostgresSslMode.VerifyFull, null));
        var observation = new PostgresEndpointObservation(IPAddress.Parse("192.0.2.1"), ["db.example.test"]);

        Assert.False(PostgresEndpointIdentity.Matches(target, "otherdb", observation, resolver));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Different_authenticated_host_fails_even_when_the_address_matches()
    {
        var resolver = new ScriptedResolver(IPAddress.Parse("192.0.2.1"));
        var target = PostgresTarget("db.example.test", false, new PostgresTransport(PostgresSslMode.VerifyFull, null));
        var observation = new PostgresEndpointObservation(IPAddress.Parse("192.0.2.1"), ["other.example.test"]);

        Assert.False(PostgresEndpointIdentity.Matches(target, "appdb", observation, resolver));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Proxy_address_outside_the_resolver_answer_fails()
    {
        var resolver = new ScriptedResolver(IPAddress.Parse("192.0.2.1"));
        var target = PostgresTarget("db.example.test", false, new PostgresTransport(PostgresSslMode.VerifyFull, null));
        var observation = new PostgresEndpointObservation(IPAddress.Parse("198.51.100.8"), ["db.example.test"]);

        Assert.False(PostgresEndpointIdentity.Matches(target, "appdb", observation, resolver));
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void Non_verifying_dns_name_is_not_authenticated_by_a_resolver_hit()
    {
        var resolver = new ScriptedResolver(IPAddress.Parse("192.0.2.1"));
        var require = PostgresTarget("db.example.test", false, new PostgresTransport(PostgresSslMode.Require, null));
        var legacy = PostgresTarget("db.example.test", false, null);
        var observation = new PostgresEndpointObservation(IPAddress.Parse("192.0.2.1"), ["db.example.test"]);

        Assert.False(PostgresEndpointIdentity.Matches(require, "appdb", observation, resolver));
        Assert.False(PostgresEndpointIdentity.Matches(legacy, "appdb", observation, resolver));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Non_verifying_ip_literal_matches_only_the_connected_address()
    {
        var resolver = new ScriptedResolver { Throw = true };
        var target = PostgresTarget("192.0.2.10,5432", true, new PostgresTransport(PostgresSslMode.Disable, null));
        var same = new PostgresEndpointObservation(IPAddress.Parse("192.0.2.10"), []);
        var other = new PostgresEndpointObservation(IPAddress.Parse("192.0.2.11"), []);

        Assert.True(PostgresEndpointIdentity.Matches(target, "appdb", same, resolver));
        Assert.False(PostgresEndpointIdentity.Matches(target, "appdb", other, resolver));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Ipv6_literals_match_structurally_and_mapped_ipv4_does_not()
    {
        var resolver = new ScriptedResolver { Throw = true };
        var target = PostgresTarget("[2001:db8::10],5432", true, new PostgresTransport(PostgresSslMode.Disable, null));
        var expanded = new PostgresEndpointObservation(IPAddress.Parse("2001:db8:0:0:0:0:0:10"), []);
        Assert.True(PostgresEndpointIdentity.Matches(target, "appdb", expanded, resolver));

        var v4 = PostgresTarget("192.0.2.10", true, new PostgresTransport(PostgresSslMode.Disable, null));
        var mapped = new PostgresEndpointObservation(IPAddress.Parse("::ffff:192.0.2.10"), []);
        Assert.False(PostgresEndpointIdentity.Matches(v4, "appdb", mapped, resolver));
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData("localhost,5433")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1,5432")]
    [InlineData("[::1],5432")]
    public void Postgres_loopback_checks_the_database_and_skips_the_resolver(string server)
    {
        var resolver = new ScriptedResolver { Throw = true };
        var target = PostgresTarget(server, true, null);
        var observation = new PostgresEndpointObservation(null, []);

        Assert.True(PostgresEndpointIdentity.Matches(target, "appdb", observation, resolver));
        Assert.False(PostgresEndpointIdentity.Matches(target, "otherdb", observation, resolver));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Trailing_dot_is_not_treated_as_loopback()
    {
        var resolver = new ScriptedResolver { Throw = true };
        var target = PostgresTarget("localhost.", false, new PostgresTransport(PostgresSslMode.Require, null));

        Assert.False(PostgresEndpointIdentity.Matches(
            target, "appdb", new PostgresEndpointObservation(IPAddress.Loopback, ["localhost"]), resolver));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void Verify_full_without_certificate_names_uses_the_requested_host_only()
    {
        var resolver = new ScriptedResolver(IPAddress.Parse("192.0.2.1"));
        var verifyFull = PostgresTarget("db.example.test,5432", false, new PostgresTransport(PostgresSslMode.VerifyFull, null));
        var verifyCa = PostgresTarget("db.example.test,5432", false, new PostgresTransport(PostgresSslMode.VerifyCa, null));
        var seen = new PostgresEndpointObservation(IPAddress.Parse("192.0.2.1"), []);

        var full = PostgresEndpointIdentity.WithVerifyFullFallback(verifyFull, seen);
        var ca = PostgresEndpointIdentity.WithVerifyFullFallback(verifyCa, seen);

        Assert.Equal(["db.example.test"], full.AuthenticatedNames);
        Assert.Empty(ca.AuthenticatedNames);
        Assert.True(PostgresEndpointIdentity.Matches(verifyFull, "appdb", full, resolver));
        Assert.False(PostgresEndpointIdentity.Matches(verifyCa, "appdb", ca, resolver));
    }

    [Theory]
    [InlineData("db.example.test", true)]
    [InlineData("DB.Example.Test", true)]
    [InlineData("a.b.example.test", false)]
    [InlineData("example.test", false)]
    public void Single_label_wildcard_authenticates_only_one_label(string host, bool expected)
    {
        var resolver = new ScriptedResolver(IPAddress.Parse("192.0.2.1"));
        var target = PostgresTarget(host, false, new PostgresTransport(PostgresSslMode.VerifyCa, null));
        var observation = new PostgresEndpointObservation(IPAddress.Parse("192.0.2.1"), ["*.example.test"]);

        Assert.Equal(expected, PostgresEndpointIdentity.Matches(target, "appdb", observation, resolver));
    }

    [Fact]
    public void Certificate_names_prefer_subject_alternative_names_over_a_different_common_name()
    {
        using var alternative = TestCertificates.Create("other.example.test", "db.example.test");
        using var commonOnly = TestCertificates.Create("db.example.test");

        Assert.Equal(["db.example.test"], NpgsqlEndpointObservation.CertificateNames(alternative));
        Assert.Equal(["db.example.test"], NpgsqlEndpointObservation.CertificateNames(commonOnly));
    }

    [Fact]
    public void Npgsql_connector_exposes_the_endpoint_members_identity_reads()
    {
        var connector = typeof(NpgsqlConnection).GetProperty(
            "Connector", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(connector);
        Assert.NotNull(connector.PropertyType.GetProperty(
            "ConnectedEndPoint", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(connector.PropertyType.GetField(
            "_stream", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void System_resolver_returns_ip_literals_without_dns()
    {
        var resolver = new SystemPostgresHostResolver();
        Assert.Equal(IPAddress.Parse("192.0.2.8"), Assert.Single(resolver.Resolve("192.0.2.8")));
        Assert.Equal(IPAddress.Parse("2001:db8::8"), Assert.Single(resolver.Resolve("[2001:db8::8]")));
    }

    private static ResolvedTarget PostgresTarget(string server, bool trust, PostgresTransport? transport) =>
        new(
            server,
            "appdb",
            AuthSpec.Parse("sql", "u", "P", trust),
            "profile",
            SqlEngine.Postgres,
            transport);

    private sealed class ScriptedResolver(params IPAddress[] addresses) : IPostgresHostResolver
    {
        public int Calls { get; private set; }
        public string? Host { get; private set; }
        public bool Throw { get; init; }

        public IReadOnlyList<IPAddress> Resolve(string host)
        {
            Calls++;
            Host = host;
            if (Throw)
                throw new InvalidOperationException("resolver-was-called");
            return addresses;
        }
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

    public sealed class SessionMessageBufferTests
    {
        [Fact]
        public void Keeps_newest_arrivals_and_reports_dropped_count()
        {
            var buffer = new SessionMessageBuffer();
            for (var index = 0; index < SessionMessageBuffer.MaxMessagesPerCommand + 500; index++)
                buffer.Add($"message {index}");

            // Sliding window: a command's trailing arrivals (such as STATISTICS
            // lines) survive while older diagnostics are evicted with a counter.
            Assert.Equal(SessionMessageBuffer.MaxMessagesPerCommand, buffer.Count);
            var consumed = buffer.Consume(0);
            Assert.Equal(SessionMessageBuffer.MaxMessagesPerCommand, consumed.Messages.Count);
            Assert.Equal("message 500", consumed.Messages[0]);
            Assert.Equal($"message {SessionMessageBuffer.MaxMessagesPerCommand + 499}", consumed.Messages[^1]);
            Assert.Equal(500, consumed.OmittedMessageCount);
            Assert.Equal(0, buffer.Count);
        }

        [Fact]
        public void Character_budget_evicts_oldest_with_counter()
        {
            var buffer = new SessionMessageBuffer();
            buffer.Add(new string('a', SessionMessageBuffer.MaxCharactersPerCommand - 100));
            buffer.Add(new string('b', 100));
            buffer.Add("over the character budget");

            // The oldest (largest) arrival is evicted; the two newest fit.
            Assert.Equal(2, buffer.Count);
            var consumed = buffer.Consume(0);
            Assert.Equal(new string('b', 100), consumed.Messages[0]);
            Assert.Equal("over the character budget", consumed.Messages[1]);
            Assert.Equal(1, consumed.OmittedMessageCount);
        }

        [Fact]
        public void Single_arrival_larger_than_the_whole_budget_is_dropped()
        {
            var buffer = new SessionMessageBuffer();
            buffer.Add(new string('x', SessionMessageBuffer.MaxCharactersPerCommand + 1));

            Assert.Equal(0, buffer.Count);
            var consumed = buffer.Consume(0);
            Assert.Empty(consumed.Messages);
            Assert.Equal(1, consumed.OmittedMessageCount);
        }

        [Fact]
        public void Consume_releases_memory_between_commands_so_repetitions_stay_bounded()
        {
            var buffer = new SessionMessageBuffer();
            for (var repetition = 0; repetition < 5; repetition++)
            {
                for (var index = 0; index < 10; index++)
                    buffer.Add($"run {repetition} notice {index}");
                var consumed = buffer.Consume(0);
                Assert.Equal(10, consumed.Messages.Count);
                Assert.Equal(0, consumed.OmittedMessageCount);
                Assert.Equal(0, buffer.Count);
            }
        }

        [Fact]
        public void Consume_returns_only_the_window_and_clamps_out_of_range_start()
        {
            var buffer = new SessionMessageBuffer();
            buffer.Add("one");
            buffer.Add("two");

            var tail = buffer.Consume(1);
            Assert.Equal(["two"], tail.Messages);
            Assert.Equal(0, tail.OmittedMessageCount);
            Assert.Equal(0, buffer.Count);

            buffer.Add("three");
            Assert.Equal(["three"], buffer.Consume(-5).Messages);
            Assert.Empty(buffer.Consume(99).Messages);
        }
    }
}