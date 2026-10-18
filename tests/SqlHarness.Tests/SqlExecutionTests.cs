using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;
using SqlHarness.Tests.Journal;

using OperationPhase = SqlHarness.Core.Journal.OperationPhase;

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
        var knownSecrets = new List<string>();

        await using var session = await factory.ConnectAsync(
            new ResolvedTarget("logical", "database", AuthSpec.Parse("azure-cli", null, null, false), "profile"),
            default,
            knownSecrets);

        Assert.Equal(1, cli.RunCount);
        Assert.Equal(
            ["account", "get-access-token", "--resource", "https://database.windows.net/"],
            cli.LastArgs);
        Assert.Equal("azure-token", connector.AccessToken);
        Assert.Contains("azure-token", knownSecrets);
        Assert.Equal("profile", session.Identity.Mode);
    }

    [Fact]
    public async Task Azure_cli_token_timing_is_diagnostic_only_and_outer_connect_is_not_double_counted()
    {
        var time = new ManualTime();
        var cli = new AdvancingAzureCli(time, 7, "safe-test-token");
        var connector = new FakeConnector("server", "database");
        var factory = new TimedSessionFactory(new SqlClientSessionFactory(cli, (connectionString, accessToken, ct) =>
        {
            time.Advance(11);
            return connector.ConnectAsync(connectionString, accessToken, ct);
        }));
        var target = new ResolvedTarget("server", "database", AuthSpec.Parse("azure-cli", null, null, false), "profile");
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            await using var session = await factory.ConnectAsync(target, CancellationToken.None);

            var recorder = OperationPhaseRecorder.Current!;
            Assert.Equal(new PhaseAggregate(OperationPhase.Connect, 1, 18, 18), Assert.Single(recorder.Aggregates()));
            var events = recorder.Events();
            Assert.Equal(2, events.Count);
            Assert.Equal(7, events[0].DurationMilliseconds);
            Assert.Contains("\"authStrategy\":\"azure-cli\"", events[0].DetailJson!);
            Assert.Contains("\"attempt\":1", events[0].DetailJson!);
            Assert.DoesNotContain("safe-test-token", string.Join(" ", events.Select(item => item.DetailJson)));
        }
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
    public async Task Server_name_difference_does_not_reject_a_connection()
    {
        var connector = new FakeConnector("other-server", "database");
        var factory = new SqlClientSessionFactory(new FakeAzureCli("unused"), connector.ConnectAsync);

        await using var session = await factory.ConnectAsync(
            new ResolvedTarget("server", "database", AuthSpec.Parse("integrated", null, null, false), "direct"),
            default);

        Assert.Equal("other-server", session.Identity.ActualServer);
        Assert.False(connector.Session.Disposed);
    }

    [Theory]
    [InlineData("sql01.corp.local", "SQL01")]
    [InlineData("ag-listener", "sql-primary-03")]
    [InlineData("fog.database.windows.net", "physical-primary.database.windows.net")]
    public async Task Different_server_name_is_reported_for_aliases_and_listeners(string requested, string actual)
    {
        var connector = new FakeConnector(actual, "AppDb");
        var factory = new SqlClientSessionFactory(new FakeAzureCli("unused"), connector.ConnectAsync);
        await using var session = await factory.ConnectAsync(
            new ResolvedTarget(requested, "AppDb", AuthSpec.Parse("integrated", null, null, false), "profile"),
            default);

        Assert.Equal(requested, session.Identity.RequestedServer);
        Assert.Equal(actual, session.Identity.ActualServer);
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

    [Theory]
    [InlineData("db.example.test", "192.0.2.1/32", PostgresSslMode.Require)]
    [InlineData("db.example.test", "192.0.2.1/32", PostgresSslMode.VerifyFull)]
    public void Postgres_dns_host_is_accepted_and_observed_address_is_reported(
        string requestedServer, string actualServer, PostgresSslMode sslMode)
    {
        var target = PostgresTarget(requestedServer, false, new PostgresTransport(sslMode, null));

        Assert.True(PostgresEndpointIdentity.Matches(target, actualServer, "appdb"));
        var report = PostgresEndpointIdentity.CreateReport(target, actualServer, "appdb");
        Assert.Equal(requestedServer, report.RequestedServer);
        Assert.Equal(actualServer, report.ActualServer);
        Assert.Equal(PostgresTransportPolicy.Label(target), report.TransportPolicy);
    }

    [Fact]
    public void Postgres_database_mismatch_remains_ordinal()
    {
        var target = PostgresTarget("db.example.test", false, new PostgresTransport(PostgresSslMode.Require, null));

        Assert.False(PostgresEndpointIdentity.Matches(target, "198.51.100.8", "otherdb"));
        Assert.False(PostgresEndpointIdentity.Matches(target, "198.51.100.8", "APPDB"));
    }

    [Fact]
    public async Task Open_transaction_is_rolled_back_with_warning()
    {
        var cleanupCommand = string.Empty;
        await using var session = CreateCleanupSession(cleanup: (sql, _) =>
        {
            cleanupCommand = sql;
            return Task.FromResult(true);
        });

        var warnings = await session.EndLogicalBatchAsync(CancellationToken.None);

        Assert.Equal(["open transaction rolled back"], warnings);
        Assert.Contains("sys.sp_cursor_list", cleanupCommand, StringComparison.Ordinal);
        Assert.Contains("@cursor_scope = 2", cleanupCommand, StringComparison.Ordinal);
        Assert.Contains("IF @@TRANCOUNT > 0", cleanupCommand, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK TRANSACTION", cleanupCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_inside_transaction_discards_connection_with_exact_error()
    {
        var clearPoolCalls = 0;
        await using var session = CreateCleanupSession(
            execute: (_, _) => Task.FromException<ISqlReader>(new TimeoutException("command timed out")),
            cleanup: (_, _) => Task.FromResult(true),
            clearPool: _ => clearPoolCalls++);

        var exception = await Assert.ThrowsAsync<SessionCleanupException>(() =>
            BenchmarkRunner.ExecuteRawAsync(
                session,
                new SqlExecutionCommand("SELECT 1", [], 1),
                null,
                CancellationToken.None));

        Assert.Equal("open transaction could not be rolled back; connection discarded", exception.Message);
        Assert.Equal(
            [SqlClientSession.BatchTimeoutWarning, SqlClientSession.ConnectionDiscardedWarning],
            exception.Warnings);
        Assert.Equal(1, clearPoolCalls);
    }

    [Fact]
    public async Task Failed_rollback_fails_operation_and_discards_connection()
    {
        var clearPoolCalls = 0;
        await using var session = CreateCleanupSession(
            cleanup: (_, _) => Task.FromException<bool>(new InvalidOperationException("rollback failed")),
            clearPool: _ => clearPoolCalls++);

        var exception = await Assert.ThrowsAsync<SessionCleanupException>(() =>
            session.EndLogicalBatchAsync(CancellationToken.None));

        Assert.Equal("open transaction could not be rolled back; connection discarded", exception.Message);
        Assert.Equal([SqlClientSession.ConnectionDiscardedWarning], exception.Warnings);
        Assert.Equal(1, clearPoolCalls);
    }

    [Fact]
    public async Task Disposing_a_doomed_session_clears_pool_once_even_without_batch_cleanup()
    {
        var clearPoolCalls = 0;
        var session = CreateCleanupSession(
            execute: (_, _) => Task.FromException<ISqlReader>(new TimeoutException("command timed out")),
            clearPool: _ => clearPoolCalls++);

        await Assert.ThrowsAsync<TimeoutException>(() => session.ExecuteReaderAsync(
            new SqlExecutionCommand("SELECT 1", [], 1), CancellationToken.None));
        await session.DisposeAsync();

        Assert.Equal(1, clearPoolCalls);
    }

    [Fact]
    public async Task Setup_split_rolls_back_once_after_whole_setup()
    {
        var session = new CleanupTrackingSession();
        var setup = "CREATE TABLE #t (Id int); INSERT #t VALUES (@x);";

        var warnings = await SetupSqlExecution.ExecuteSetupAsync(
            SqlEngine.SqlServer,
            setup,
            [new SqlHarnessParameter("@x", System.Data.SqlDbType.Int, 7, null, null, null, null)],
            5,
            session,
            null,
            CancellationToken.None);

        Assert.Equal(2, session.ExecutedCommands);
        Assert.Equal(1, session.EndLogicalBatchCalls);
        Assert.Equal(["open transaction rolled back"], warnings);
    }

    [Fact]
    public async Task Setup_with_tvp_source_passes_its_structured_binding_to_execution()
    {
        const string setup = "CREATE TABLE #t (Id int); INSERT #t SELECT Id FROM @Ids; UPDATE #t SET Id = Id WHERE EXISTS (SELECT 1 FROM @Ids);";
        var tableParameter = new SqlHarnessParameter("@Ids", System.Data.SqlDbType.Structured, DBNull.Value, null,
            TableTypeName: "dbo.IdList");
        var safety = new SqlSafetyClassifier().Classify(
            setup,
            SqlUsage.CompareSetup,
            "db",
            allowMutation: false,
            confirmDatabase: null,
            tableValuedParameterNames: new HashSet<string>(["@Ids"], StringComparer.OrdinalIgnoreCase));
        Assert.True(safety.Allowed, safety.RejectionDescription);
        var session = new CleanupTrackingSession();

        await SetupSqlExecution.ExecuteSetupAsync(
            SqlEngine.SqlServer,
            setup,
            [tableParameter],
            5,
            session,
            null,
            CancellationToken.None,
            safety);

        Assert.Equal(2, session.Commands.Count);
        Assert.Empty(session.Commands[0].Parameters);
        Assert.Contains("INSERT #t SELECT Id FROM @Ids", session.Commands[1].Sql, StringComparison.Ordinal);
        Assert.Contains("UPDATE #t SET Id = Id", session.Commands[1].Sql, StringComparison.Ordinal);
        var bound = Assert.Single(session.Commands[1].Parameters);
        Assert.Equal("@Ids", bound.Name);
        Assert.Equal(System.Data.SqlDbType.Structured, bound.Type);
        Assert.Equal("dbo.IdList", bound.TableTypeName);
    }

    private static SqlClientSession CreateCleanupSession(
        Func<SqlExecutionCommand, CancellationToken, Task<ISqlReader>>? execute = null,
        Func<string, CancellationToken, Task<bool>>? cleanup = null,
        Action<SqlConnection>? clearPool = null) =>
        new(
            new SqlConnection(),
            new SessionMessageBuffer(),
            (_, _) => { },
            execute,
            cleanup,
            clearPool);

    private sealed class CleanupTrackingSession : ISqlSession
    {
        public IReadOnlyList<SessionMessage> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;
        public int ExecutedCommands { get; private set; }
        public int EndLogicalBatchCalls { get; private set; }
        public List<SqlExecutionCommand> Commands { get; } = [];

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            ExecutedCommands++;
            Commands.Add(command);
            return Task.FromResult<ISqlReader>(new CleanupEmptyReader());
        }

        public Task<IReadOnlyList<string>> EndLogicalBatchAsync(CancellationToken ct)
        {
            EndLogicalBatchCalls++;
            return Task.FromResult<IReadOnlyList<string>>(["open transaction rolled back"]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CleanupEmptyReader : ISqlReader
    {
        public int FieldCount => 0;
        public int RecordsAffected => 0;
        public string GetName(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public Type GetFieldType(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public bool GetAllowNull(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public object GetValue(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ResolvedTarget PostgresTarget(string server, bool trust, PostgresTransport? transport) =>
        new(
            server,
            "appdb",
            AuthSpec.Parse("sql", "u", "P", trust),
            "profile",
            SqlEngine.Postgres,
            transport);
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

    private sealed class AdvancingAzureCli(ManualTime time, int milliseconds, string token) : IAzureCli
    {
        public Task<bool> IsLoggedInAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<JsonElement> RunJsonAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            time.Advance(milliseconds);
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
        public IReadOnlyList<SessionMessage> Messages => [];
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
            Assert.Equal("message 500", consumed.Messages[0].Text);
            Assert.Equal($"message {SessionMessageBuffer.MaxMessagesPerCommand + 499}", consumed.Messages[^1].Text);
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
            Assert.Equal(new string('b', 100), consumed.Messages[0].Text);
            Assert.Equal("over the character budget", consumed.Messages[1].Text);
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
            Assert.Equal(["two"], tail.Messages.Select(message => message.Text));
            Assert.Equal(0, tail.OmittedMessageCount);
            Assert.Equal(0, buffer.Count);

            buffer.Add("three");
            Assert.Equal(["three"], buffer.Consume(-5).Messages.Select(message => message.Text));
            Assert.Empty(buffer.Consume(99).Messages);
        }
    }
}
