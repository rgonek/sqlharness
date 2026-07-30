using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class SnapshotTests
{
    private const string Token = "fake-access-token-never-emit";
    private const string Name = "before-import";

    [Fact]
    public async Task Snapshot_capture_stores_complete_bounded_result()
    {
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1, 2);
        var outcome = await Module(session, store).ExecuteAsync(Snapshot());

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessSnapshotReport>(outcome.Report);
        Assert.Equal(SnapshotVerdict.Stored, report.Verdict);
        Assert.Equal(Name, report.Name);
        Assert.Equal(0, report.DifferenceCount);
        Assert.Empty(report.Differences);

        var saved = Assert.Single(store.Saves);
        Assert.Equal(Name, saved.Name);
        Assert.False(saved.Force);
        Assert.Equal(2, saved.Document.ResultSets[0].Rows.Count);
        Assert.Equal(1, session.ConnectCount);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task Snapshot_force_overwrites_existing_snapshot()
    {
        var store = new FakeSnapshotStore(SnapshotFixture.Document(rows: [[9]]));
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store).ExecuteAsync(Snapshot(force: true));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(SnapshotVerdict.Stored, Assert.IsType<SqlHarnessSnapshotReport>(outcome.Report).Verdict);
        var saved = Assert.Single(store.Saves);
        Assert.True(saved.Force);
        Assert.Equal(1, SnapshotScalarValue(saved.Document.ResultSets[0].Rows[0][0]));
    }

    [Fact]
    public async Task Snapshot_without_force_rejects_existing_as_local_storage()
    {
        var store = new FakeSnapshotStore(SnapshotFixture.Document(rows: [[9]]));
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store).ExecuteAsync(Snapshot(force: false));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(store.Saves);
        Assert.Equal(1, session.ConnectCount);
    }

    [Fact]
    public async Task Snapshot_diff_identical_returns_zero()
    {
        var store = new FakeSnapshotStore(SnapshotFixture.Document(rows: [[1]]));
        var outcome = await Module(FakeSession.WithRows(1), store)
            .ExecuteAsync(Snapshot(diff: true));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessSnapshotReport>(outcome.Report);
        Assert.Equal(SnapshotVerdict.Identical, report.Verdict);
        Assert.Equal(0, report.DifferenceCount);
        Assert.Empty(report.Differences);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task Snapshot_diff_returns_eight_and_only_compact_locations()
    {
        var store = new FakeSnapshotStore(SnapshotFixture.Document(rows: [[1]]));
        var outcome = await Module(FakeSession.WithRows(2), store)
            .ExecuteAsync(Snapshot(diff: true));

        Assert.Equal(SqlHarnessExitCode.SnapshotDifferences, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessSnapshotReport>(outcome.Report);
        Assert.Equal(SnapshotVerdict.Different, report.Verdict);
        Assert.Equal(1, report.DifferenceCount);
        var difference = Assert.Single(report.Differences);
        Assert.Equal(new SqlHarnessSnapshotDifference(0, 0, 0, "cell-changed"), difference);
        Assert.DoesNotContain("1", JsonSerializer.Serialize(report.Differences), StringComparison.Ordinal);
        Assert.DoesNotContain("2", JsonSerializer.Serialize(report.Differences), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_shape_change_returns_safety_without_cell_locations()
    {
        var store = new FakeSnapshotStore(
            SnapshotFixture.Document(columns: [("Other", "System.Int32")], rows: [[1]]));
        var outcome = await Module(FakeSession.WithRows(1), store)
            .ExecuteAsync(Snapshot(diff: true));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("shape", outcome.SafeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_missing_baseline_is_local_storage_without_connection()
    {
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store).ExecuteAsync(Snapshot(diff: true));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Equal(0, session.ConnectCount);
        Assert.Empty(session.Commands);
        Assert.Equal(1, store.LoadCalls);
    }

    [Fact]
    public async Task Snapshot_corrupt_baseline_is_local_storage_without_connection()
    {
        var store = new FakeSnapshotStore { LoadFailure = new InvalidDataException("Snapshot 'broken' is malformed.") };
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store).ExecuteAsync(Snapshot(diff: true));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Equal(0, session.ConnectCount);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Snapshot_rejects_omitted_rows_with_exact_message()
    {
        var store = new FakeSnapshotStore();
        // max-rows 1 but two rows returned → OmittedRowCount > 0
        var session = FakeSession.WithRows(1, 2);
        var outcome = await Module(session, store)
            .ExecuteAsync(Snapshot(maxRows: 1));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(
            "Snapshot result exceeded --max-rows; raise the explicit bound or narrow the query.",
            outcome.SafeError);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task Snapshot_rejects_mutation_sql_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store, azure: azure)
            .ExecuteAsync(Snapshot(sql: "DELETE FROM dbo.T"));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
        Assert.Equal(0, store.LoadCalls);
    }

    [Fact]
    public async Task Snapshot_rejects_unreferenced_parameter_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store, azure: azure)
            .ExecuteAsync(Snapshot() with { Parameters = ["ClinetId:int=42"] });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public async Task Snapshot_rejects_invalid_bounds_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store, azure: azure)
            .ExecuteAsync(Snapshot(maxRows: -1));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public async Task Snapshot_rejects_invalid_label_before_authentication()
    {
        var azure = new FakeAzureCli(Token);
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store, azure: azure)
            .ExecuteAsync(Snapshot() with { Name = "bad name" });

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(azure.Calls);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public async Task Snapshot_sql_failure_maps_to_five()
    {
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        session.ExecuteFailure = new TimeoutException($"timeout {Token}");
        var outcome = await Module(session, store).ExecuteAsync(Snapshot());

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task Snapshot_authentication_failure_maps_to_three()
    {
        var azure = new FakeAzureCli(Token, new AzureCliException($"not logged in {Token}"));
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store, azure: azure).ExecuteAsync(Snapshot());

        Assert.Equal(SqlHarnessExitCode.Authentication, outcome.ExitCode);
        Assert.Empty(session.Commands);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_target_mismatch_maps_to_four_without_user_sql()
    {
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        var outcome = await Module(
                session,
                store,
                connectFailure: new SqlTargetMismatchException($"mismatch {Token}"))
            .ExecuteAsync(Snapshot());

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Empty(session.Commands);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_gain_receipt_uses_snapshot_command_name()
    {
        var gain = new FakeGainStore();
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        var outcome = await Module(session, store, gain: gain).ExecuteAsync(Snapshot());

        Assert.Empty(gain.Records);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(10, 1));

        var record = Assert.Single(gain.Records);
        Assert.Equal("snapshot", record.Command);
        Assert.True(record.Success);
        Assert.True(record.RawBytes > 0);
        Assert.Equal(10, record.EmittedBytes);
    }

    [Fact]
    public async Task Snapshot_redacts_secrets_from_errors()
    {
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(1);
        session.ExecuteFailure = new InvalidOperationException($"failed with {Token}");
        var outcome = await Module(session, store).ExecuteAsync(Snapshot());

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.DoesNotContain(Token, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    private static SqlHarnessModule Module(
        FakeSession session,
        ISnapshotStore store,
        FakeAzureCli? azure = null,
        FakeGainStore? gain = null,
        Exception? connectFailure = null,
        Func<IReadOnlyDictionary<string, TargetProfile>>? loadProfiles = null) =>
        new(
            new FakeSessionFactory(session, azure ?? new FakeAzureCli(Token), connectFailure),
            gain ?? new FakeGainStore(),
            loadProfiles ?? Profiles,
            watchClock: null,
            snapshotStore: store);

    private static SqlHarnessSnapshotOperation Snapshot(
        string sql = "SELECT 1 AS Value",
        bool diff = false,
        bool force = false,
        int maxRows = 50) =>
        new(
            Target(),
            sql,
            [],
            TimeoutSeconds: 30,
            MaxRows: maxRows,
            Name: Name,
            Diff: diff,
            Force: force);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static int SnapshotScalarValue(SnapshotScalar scalar) =>
        scalar.Value.GetInt32();

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

    private sealed class FakeGainStore : IGainStore
    {
        public List<GainRecord> Records { get; } = [];

        public void Append(GainRecord record) => Records.Add(record);

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSessionFactory(
        FakeSession session,
        FakeAzureCli azure,
        Exception? connectFailure) : ISqlSessionFactory
    {
        public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            if (connectFailure is not null)
                throw connectFailure;
            var tokenResponse = await azure.RunJsonAsync(
                ["account", "get-access-token", "--resource", "https://database.windows.net/"], ct);
            session.FactoryAccessToken = tokenResponse.GetProperty("accessToken").GetString();
            session.ConnectCount++;
            if (!string.Equals(target.Server, session.Identity.ActualServer, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.Database, session.Identity.ActualDatabase, StringComparison.Ordinal))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");
            return session;
        }
    }

    private sealed class FakeSession : ISqlSession
    {
        private readonly Queue<Func<ISqlReader>> _results;

        private FakeSession(IEnumerable<Func<ISqlReader>> results)
        {
            _results = new Queue<Func<ISqlReader>>(results);
        }

        public int ConnectCount { get; set; }
        public string? FactoryAccessToken { get; set; }
        public Exception? ExecuteFailure { get; set; }
        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        public static FakeSession WithRows(params object[] values)
        {
            var rows = values.Select(value => new object?[] { value }).ToArray();
            ISqlReader reader = FakeReader.Rows(["Value"], rows);
            return new FakeSession([(Func<ISqlReader>)(() => reader)]);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (ExecuteFailure is not null)
                return Task.FromException<ISqlReader>(ExecuteFailure);
            if (_results.Count == 0)
                throw new InvalidOperationException("No more fake results queued.");
            return Task.FromResult(_results.Dequeue()());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader((string[] Names, object?[][] Rows)[] sets) : ISqlReader
    {
        private int _set;
        private int _position = -1;

        public static FakeReader Rows(string[] names, params object?[][] rows) =>
            new([(names, rows)]);

        private string[] Names => sets[_set].Names;
        private object?[][] RowsData => sets[_set].Rows;

        public int FieldCount => Names.Length;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => Names[ordinal];
        public Type GetFieldType(int ordinal) =>
            RowsData.FirstOrDefault()?[ordinal]?.GetType() ?? typeof(int);
        // Match SnapshotFixture default AllowNull so preloaded baselines compare cleanly.
        public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal) => RowsData[_position][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            var hasRow = ++_position < RowsData.Length;
            return Task.FromResult(hasRow);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (++_set < sets.Length)
            {
                _position = -1;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        private SnapshotDocument? _document;

        public FakeSnapshotStore(SnapshotDocument? existing = null) => _document = existing;

        public Exception? LoadFailure { get; set; }
        public List<(string Name, SnapshotDocument Document, bool Force)> Saves { get; } = [];
        public int LoadCalls { get; private set; }

        public void Save(string name, SnapshotDocument document, bool force)
        {
            if (!force && _document is not null)
                throw new IOException($"Snapshot '{name}' already exists.");
            Saves.Add((name, document, force));
            _document = document;
        }

        public SnapshotDocument Load(string name)
        {
            LoadCalls++;
            if (LoadFailure is not null)
                throw LoadFailure;
            if (_document is null)
                throw new FileNotFoundException($"Snapshot '{name}' was not found.");
            return _document;
        }
    }

    private static class SnapshotFixture
    {
        public static SnapshotDocument Document(
            (string Name, string DataType)[]? columns = null,
            object?[][]? rows = null,
            DateTimeOffset? createdAt = null,
            string resultHash = "HASH")
        {
            columns ??= [("Value", "System.Int32")];
            rows ??= [[1]];
            var columnReports = columns
                .Select((column, ordinal) =>
                    new SqlHarnessColumnReport(ordinal, column.Name, column.DataType, AllowNull: true))
                .ToArray();
            var scalarRows = rows
                .Select(row => (IReadOnlyList<SnapshotScalar>)row.Select(SnapshotScalar.FromValue).ToArray())
                .ToArray();
            return new SnapshotDocument(
                Version: 1,
                CreatedAt: createdAt ?? new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
                ResultSets: [new SnapshotResultSet(columnReports, scalarRows, scalarRows.Length)],
                ResultHash: resultHash);
        }
    }
}
