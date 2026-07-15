using System.Data;
using System.Text.Json;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;
using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SqlHarnessQueryTests
{
    private const string Token = "fake-access-token-never-emit";

    [Fact]
    public async Task Target_mismatch_stops_before_user_sql()
    {
        var session = FakeSqlSession.WithIdentity("wrong-server", "master");

        var outcome = await Module(session).ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Factory_identity_is_used_before_user_command()
    {
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            FakeSqlReader.Rows(["Value"], [1]));

        var outcome = await Module(session).ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal("SELECT 1", Assert.Single(session.Commands).Sql);
        Assert.Equal("profile", Assert.IsType<SqlHarnessQueryReport>(outcome.Report).Target.Mode);
    }

    [Fact]
    public async Task Query_rejects_a_supplied_parameter_not_referenced_by_the_batch_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var session = FakeSqlSession.WithIdentity("test-server", "testdb-a");

        var outcome = await Module(session, azure: azure).ExecuteAsync(
            Query("SELECT @ClientId") with { Parameters = ["ClinetId:int=42"] });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Query_parameter_reference_matching_is_case_insensitive_and_does_not_rewrite_SQL()
    {
        const string sql = "SELECT @clientid AS Value";
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            FakeSqlReader.Rows(["Value"], [42]));

        var outcome = await Module(session).ExecuteAsync(
            Query(sql) with { Parameters = ["CLIENTID:int=42"] });

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(sql, session.Commands[0].Sql);
        Assert.Equal("@CLIENTID", Assert.Single(session.Commands[0].Parameters).Name);
    }

    [Fact]
    public async Task Select_reports_read_only_classification_even_when_mutation_flags_are_present()
    {
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            FakeSqlReader.Rows(["Value"], [1]));

        var outcome = await Module(session).ExecuteAsync(Query("SELECT 1") with
        {
            AllowMutation = true,
            ConfirmDatabase = "testdb-a",
        });

        Assert.Equal("read-only", Assert.IsType<SqlHarnessQueryReport>(outcome.Report).StatementClassification);
    }

    [Fact]
    public async Task Authentication_uses_exact_Azure_SQL_resource_and_token_is_never_reported()
    {
        var azure = new FakeAzureCli(Token);
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            failure: new InvalidOperationException($"Login failed for token {Token}"));

        var outcome = await Module(session, azure: azure).ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(
            ["account", "get-access-token", "--resource", "https://database.windows.net/"],
            Assert.Single(azure.Calls));
        Assert.Equal(Token, session.FactoryAccessToken);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_consumes_all_rows_but_retains_only_global_display_limit()
    {
        var reader = FakeSqlReader.Rows(["Value"], [1], [2], [3], [4], [5]);
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            reader);

        var outcome = await Module(session).ExecuteAsync(Query("SELECT Value", maxRows: 2));

        var report = Assert.IsType<SqlHarnessQueryReport>(outcome.Report);
        var resultSet = Assert.Single(report.ResultSets);
        Assert.Equal(5, resultSet.RowCount);
        Assert.Equal(2, resultSet.Rows.Count);
        Assert.Equal(3, resultSet.OmittedRowCount);
        Assert.Equal(6, reader.ReadCalls);
    }

    [Fact]
    public async Task Timeout_maps_to_stable_SQL_execution_exit_code()
    {
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            failure: new TimeoutException($"Timeout running SELECT 1 with {Token}"));

        var outcome = await Module(session).ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT 1", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_failure_receipt_preserves_exact_partial_raw_rows_and_messages()
    {
        const string message = "safe diagnostic before reader failure";
        var gain = new FakeGainStore();
        var messages = new List<string>();
        var reader = FakeSqlReader.RowsThenFail(["Value"], 1, () => messages.Add(message), [1], [2]);
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            reader,
            messages: messages);

        var outcome = await Module(session, gain: gain).ExecuteAsync(Query("SELECT Value"));
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(1, 1));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        using var expected = new CanonicalResultAccumulator();
        expected.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        expected.AddRow([1]);
        expected.EndResultSet();
        expected.AddMessage("sql", message);
        var completed = expected.Complete().Footprint;
        var record = Assert.Single(gain.Records);
        Assert.Equal(completed.Bytes - 2, record.RawBytes);
        Assert.Equal(completed.Lines, record.RawLines);
    }

    [Fact]
    public async Task Query_captures_message_emitted_during_read_in_report_hash_and_raw_footprint()
    {
        const string message = "late message from ReadAsync";
        var messages = new List<string> { "earlier identity message" };
        var reader = FakeSqlReader.RowsWithMessage(["Value"], () => messages.Add(message), [1]);
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            reader,
            messages: messages);

        var outcome = await Module(session).ExecuteAsync(Query("SELECT Value"));

        var report = Assert.IsType<SqlHarnessQueryReport>(outcome.Report);
        Assert.Equal([message], report.Messages);
        using var expected = new CanonicalResultAccumulator();
        expected.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        expected.AddRow([1]);
        expected.EndResultSet();
        expected.AddMessage("sql", message);
        var canonical = expected.Complete();
        Assert.Equal(canonical.Hash, report.ResultHash);
        Assert.Equal(canonical.Footprint, report.RawFootprint);
    }

    [Fact]
    public async Task Gain_is_deferred_until_receipt_completion_and_receives_exact_emitted_footprint()
    {
        var gain = new FakeGainStore();
        var successSession = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            FakeSqlReader.Rows(["Value"], [1]));
        var failureSession = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            failure: new TimeoutException(Token));

        var success = await Module(successSession, gain: gain).ExecuteAsync(Query("SELECT 1"));
        var failure = await Module(failureSession, gain: gain).ExecuteAsync(Query("SELECT 2"));

        Assert.Empty(gain.Records);
        Assert.Equal(SqlHarnessExitCode.Success, await Assert.IsType<SqlHarnessEmissionReceipt>(success.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(40, 2)));
        Assert.Equal(SqlHarnessExitCode.SqlExecution, await Assert.IsType<SqlHarnessEmissionReceipt>(failure.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(12, 1)));

        Assert.Collection(
            gain.Records,
            record =>
            {
                Assert.True(record.Success);
                Assert.Equal(40, record.EmittedBytes);
                Assert.Equal(2, record.EmittedLines);
            },
            record =>
            {
                Assert.False(record.Success);
                Assert.Equal(12, record.EmittedBytes);
                Assert.Equal(1, record.EmittedLines);
            });
        Assert.All(gain.Records, record => Assert.Equal("query", record.Command));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(gain.Records), StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", JsonSerializer.Serialize(gain.Records), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipt_completion_is_exactly_once_and_thread_safe()
    {
        var gain = new FakeGainStore();
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            FakeSqlReader.Rows(["Value"], [1]));
        var outcome = await Module(session, gain: gain).ExecuteAsync(Query("SELECT 1"));
        var receipt = Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => receipt.CompleteAsync(new OutputFootprint(24, 1))));

        Assert.All(results, code => Assert.Equal(SqlHarnessExitCode.Success, code));
        Assert.Single(gain.Records);
    }

    [Fact]
    public async Task Receipt_maps_gain_storage_failure_without_leaking_exception()
    {
        var gain = new FakeGainStore(new IOException($"disk failure {Token}"));
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            FakeSqlReader.Rows(["Value"], [1]));
        var outcome = await Module(session, gain: gain).ExecuteAsync(Query("SELECT 1"));

        var completion = await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(8, 1));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, completion);
    }

    [Fact]
    public async Task Invalid_negative_footprint_does_not_cache_receipt_completion()
    {
        var gain = new FakeGainStore();
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            FakeSqlReader.Rows(["Value"], [1]));
        var outcome = await Module(session, gain: gain).ExecuteAsync(Query("SELECT 1"));
        var receipt = Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await receipt.CompleteAsync(new OutputFootprint(-1, -1)));
        var validCompletion = await receipt.CompleteAsync(new OutputFootprint(16, 1));

        Assert.Equal(SqlHarnessExitCode.Success, validCompletion);
        Assert.Single(gain.Records);
        Assert.Equal(16, gain.Records[0].EmittedBytes);
    }

    [Fact]
    public async Task Invalid_cloned_footprint_is_rejected_before_receipt_completion_is_cached()
    {
        var gain = new FakeGainStore();
        var session = FakeSqlSession.WithIdentity(
            "test-server",
            "testdb-a",
            FakeSqlReader.Rows(["Value"], [1]));
        var outcome = await Module(session, gain: gain).ExecuteAsync(Query("SELECT 1"));
        var receipt = Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt);
        var invalid = new OutputFootprint(1, 1) with { Bytes = -1 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await receipt.CompleteAsync(invalid));
        var validCompletion = await receipt.CompleteAsync(new OutputFootprint(20, 1));

        Assert.Equal(SqlHarnessExitCode.Success, validCompletion);
        Assert.Single(gain.Records);
        Assert.Equal(20, gain.Records[0].EmittedBytes);
    }

    [Fact]
    public async Task Azure_token_failure_maps_to_authentication_and_does_not_open_a_session()
    {
        var azure = new FakeAzureCli(Token, new AzureCliException($"not logged in {Token}"));
        var session = FakeSqlSession.WithIdentity("test-server", "testdb-a");

        var outcome = await Module(session, azure: azure).ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.Authentication, outcome.ExitCode);
        Assert.Empty(session.Commands);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sql_exception_during_connect_maps_to_authentication_but_execution_maps_to_sql_execution()
    {
        var connectOutcome = await Module(
                FakeSqlSession.WithIdentity("test-server", "testdb-a"),
                connectFailure: FakeSqlException())
            .ExecuteAsync(Query("SELECT 1"));
        var executionOutcome = await Module(
                FakeSqlSession.WithIdentity("test-server", "testdb-a", failure: FakeSqlException()))
            .ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.Authentication, connectOutcome.ExitCode);
        Assert.Equal(SqlHarnessExitCode.SqlExecution, executionOutcome.ExitCode);
    }

    [Fact]
    public async Task Typed_target_mismatch_maps_to_four_without_running_user_sql()
    {
        var session = FakeSqlSession.WithIdentity("test-server", "testdb-a");
        var gain = new FakeGainStore();
        var outcome = await Module(
                session,
                gain: gain,
                connectFailure: new SqlTargetMismatchException($"different target access_token={Token}"))
            .ExecuteAsync(Query("SELECT 1"));
        var receipt = Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt);
        var first = await receipt.CompleteAsync(new OutputFootprint(4, 1));
        var second = await receipt.CompleteAsync(new OutputFootprint(99, 9));

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Equal(SqlHarnessExitCode.TargetMismatch, first);
        Assert.Equal(first, second);
        Assert.Single(gain.Records);
        Assert.Equal(4, gain.Records[0].EmittedBytes);
        Assert.Empty(session.Commands);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_message_containing_identity_phrase_remains_safety()
    {
        var outcome = await Module(
                FakeSqlSession.WithIdentity("test-server", "testdb-a"),
                loadProfiles: () => throw new SqlHarnessSafetyException("profile identity does not match rule"))
            .ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_load_io_failures_map_to_local_storage_and_are_redacted(bool unauthorized)
    {
        var secret = "access-token-profile-secret-never-emit";
        Exception failure = unauthorized
            ? new UnauthorizedAccessException($"denied {secret}")
            : new IOException($"failed {secret}");
        var outcome = await Module(
                FakeSqlSession.WithIdentity("test-server", "testdb-a"),
                loadProfiles: () => throw failure)
            .ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.DoesNotContain(secret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_loads_profiles_exactly_once()
    {
        var loads = 0;
        var outcome = await Module(
                FakeSqlSession.WithIdentity("test-server", "testdb-a", FakeSqlReader.Rows(["Value"], [1])),
                loadProfiles: () =>
                {
                    loads++;
                    return Profiles();
                })
            .ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Malformed_profile_error_remains_safety()
    {
        var outcome = await Module(
                FakeSqlSession.WithIdentity("test-server", "testdb-a"),
                loadProfiles: () => throw new SqlHarnessSafetyException("Could not parse target profiles file."))
            .ExecuteAsync(Query("SELECT 1"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
    }

    [Fact]
    public async Task Gain_returns_the_aggregate_without_loading_profiles_or_opening_SQL()
    {
        var session = FakeSqlSession.WithIdentity("test-server", "testdb-a");
        var gain = new FakeGainStore(aggregate: EmptyGainReport());

        var outcome = await Module(session, gain: gain).ExecuteAsync(new SqlHarnessGainOperation());

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Same(gain.AggregateResult, outcome.Report);
        Assert.Empty(session.Commands);
        Assert.Null(outcome.EmissionReceipt);
    }

    [Fact]
    public async Task Gain_storage_failure_redacts_every_aggregate_exception_branch()
    {
        var secret = "gain-secret-never-emit";
        var failure = new AggregateException(
            new IOException($"Password={secret}"),
            new InvalidOperationException($"access_token={secret}"));
        var gain = new FakeGainStore(aggregateFailure: failure);

        var outcome = await Module(FakeSqlSession.WithIdentity("test-server", "testdb-a"), gain: gain)
            .ExecuteAsync(new SqlHarnessGainOperation());

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.DoesNotContain(secret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    private static SqlHarnessModule Module(
        FakeSqlSession session,
        FakeAzureCli? azure = null,
        FakeGainStore? gain = null,
        Exception? connectFailure = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? loadProfiles = null) =>
        new(new FakeSqlSessionFactory(session, azure ?? new FakeAzureCli(Token), connectFailure), gain ?? new FakeGainStore(), loadProfiles ?? Profiles);

    private static Microsoft.Data.SqlClient.SqlException FakeSqlException() =>
        (Microsoft.Data.SqlClient.SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(Microsoft.Data.SqlClient.SqlException));

    private static SqlHarnessQueryOperation Query(string sql, int maxRows = 50) =>
        new(Target(), sql, [], 30, maxRows, false, null);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private sealed class FakeAzureCli(string token, Exception? failure = null) : IAzureCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<JsonElement> RunJsonAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args.ToArray());
            if (failure is not null)
                return Task.FromException<JsonElement>(failure);

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { accessToken = token }));
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private static SqlHarnessGainReport EmptyGainReport()
    {
        var empty = new SqlHarnessGainSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new(empty, empty, empty) { Measure = empty };
    }

    private sealed class FakeGainStore(
        Exception? failure = null,
        SqlHarnessGainReport? aggregate = null,
        Exception? aggregateFailure = null) : IGainStore
    {
        public List<GainRecord> Records { get; } = [];
        public SqlHarnessGainReport? AggregateResult => aggregate;
        public void Append(GainRecord record)
        {
            if (failure is not null)
                throw failure;
            Records.Add(record);
        }
        public SqlHarnessGainReport Aggregate() =>
            aggregateFailure is not null ? throw aggregateFailure : aggregate ?? throw new NotSupportedException();
    }

    private sealed class FakeSqlSessionFactory(FakeSqlSession session, FakeAzureCli azure, Exception? connectFailure) : ISqlSessionFactory
    {
        public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            if (connectFailure is not null)
                throw connectFailure;
            var tokenResponse = await azure.RunJsonAsync(
                ["account", "get-access-token", "--resource", "https://database.windows.net/"], ct);
            var accessToken = tokenResponse.GetProperty("accessToken").GetString()!;
            session.FactoryTarget = target;
            session.FactoryAccessToken = accessToken;
            if (!string.Equals(target.Server, session.Identity.ActualServer, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.Database, session.Identity.ActualDatabase, StringComparison.Ordinal))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");
            return session;
        }
    }

    private sealed class FakeSqlSession : ISqlSession
    {
        private readonly Queue<Func<ISqlReader>> _results;
        private readonly IReadOnlyList<string> _messages;

        private FakeSqlSession(IEnumerable<Func<ISqlReader>> results, IReadOnlyList<string>? messages = null)
        {
            _results = new(results);
            _messages = messages ?? [];
        }

        public List<SqlExecutionCommand> Commands { get; } = [];
        public ResolvedTarget? FactoryTarget { get; set; }
        public string? FactoryAccessToken { get; set; }
        public IReadOnlyList<string> Messages => _messages.ToArray();
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;

        public static FakeSqlSession WithIdentity(
            string server,
            string database,
            FakeSqlReader? userReader = null,
            Exception? failure = null,
            IReadOnlyList<string>? messages = null)
        {
            var results = new List<Func<ISqlReader>>();
            if (failure is not null)
                results.Add(() => throw failure);
            else if (userReader is not null)
                results.Add(() => userReader);
            return new FakeSqlSession(results, messages)
            {
                Identity = new("test-server", "testdb-a", server, database, "profile"),
            };
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(_results.Dequeue()());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSqlReader(
        string[] names,
        object?[][] rows,
        int? failAfterSuccessfulReads = null,
        Action? onSuccessfulRead = null) : ISqlReader
    {
        private int _position = -1;
        public int ReadCalls { get; private set; }
        public int FieldCount => names.Length;
        public int RecordsAffected => -1;

        public static FakeSqlReader Rows(string[] names, params object?[][] rows) => new(names, rows);
        public static FakeSqlReader RowsThenFail(
            string[] names,
            int failAfterRows,
            Action onSuccessfulRead,
            params object?[][] rows) => new(names, rows, failAfterRows, onSuccessfulRead);
        public static FakeSqlReader RowsWithMessage(
            string[] names,
            Action onSuccessfulRead,
            params object?[][] rows) => new(names, rows, onSuccessfulRead: onSuccessfulRead);
        public string GetName(int ordinal) => names[ordinal];
        public Type GetFieldType(int ordinal) => rows.FirstOrDefault()?[ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => rows.Any(row => row[ordinal] is null or DBNull);
        public object GetValue(int ordinal) => rows[_position][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            ReadCalls++;
            if (failAfterSuccessfulReads == _position + 1)
                throw new TimeoutException("reader failed after partial output");
            var hasRow = ++_position < rows.Length;
            if (hasRow)
                onSuccessfulRead?.Invoke();
            return Task.FromResult(hasRow);
        }

        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
