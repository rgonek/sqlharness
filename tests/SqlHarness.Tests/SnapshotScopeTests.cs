using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

// 003/T4: scoped snapshot capture/diff. A scoped (MCP) operation carries the
// frozen scope owner: captures stamp it, captures/diffs over a foreign or
// ownerless baseline are refused before any connection, and diff output never
// carries cell values. Unscoped (CLI) operations pass no owner and keep
// working by name; the file format stays additive (no "owner" key when null).
public class SnapshotScopeTests
{
    private const string Refusal = "The snapshot is not available in the current scope.";

    private static ArtifactOwner ScopeOwner() => new(
        "test",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["env"] = "a" },
        "sqlserver",
        "test-server",
        "testdb-a");

    private static ArtifactOwner ForeignOwner() => new(
        "other-scope",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "other" },
        "sqlserver",
        "foreign-server",
        "foreigndb");

    [Fact]
    public async Task Scoped_capture_stamps_scope_owner_on_saved_document()
    {
        var store = new FakeSnapshotStore();
        var session = FakeSession.WithRows(424242);
        var owner = ScopeOwner();

        var outcome = await Module(session, store).ExecuteAsync(Snapshot(owner));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var saved = Assert.Single(store.Saves);
        Assert.True(owner.Matches(saved.Document.Owner));
        Assert.Equal("test", saved.Document.Owner!.Profile);
        Assert.Equal("sqlserver", saved.Document.Owner.Engine);
        Assert.Equal("test-server", saved.Document.Owner.Server);
        Assert.Equal("testdb-a", saved.Document.Owner.Database);
        Assert.Equal(1, session.ConnectCount);
    }

    [Fact]
    public async Task Scoped_capture_over_foreign_snapshot_refuses_before_connection()
    {
        var store = new FakeSnapshotStore(OwnedDocument(ForeignOwner(), rows: [[8675309]]));
        var session = FakeSession.WithRows(1);

        var outcome = await Module(session, store).ExecuteAsync(Snapshot(ScopeOwner()));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(Refusal, outcome.SafeError);
        Assert.DoesNotContain("8675309", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, session.ConnectCount);
        Assert.Empty(session.Commands);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task Scoped_capture_over_legacy_snapshot_refuses_before_connection()
    {
        var store = new FakeSnapshotStore(Document(rows: [[8675309]]));
        var session = FakeSession.WithRows(1);

        var outcome = await Module(session, store).ExecuteAsync(Snapshot(ScopeOwner()));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(Refusal, outcome.SafeError);
        Assert.Equal(0, session.ConnectCount);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task Scoped_capture_of_own_name_without_force_keeps_no_overwrite()
    {
        var store = new FakeSnapshotStore(OwnedDocument(ScopeOwner(), rows: [[8675309]]));
        var session = FakeSession.WithRows(1);

        var outcome = await Module(session, store).ExecuteAsync(Snapshot(ScopeOwner(), force: false));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(store.Saves);
        Assert.DoesNotContain("8675309", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_diff_of_foreign_snapshot_refuses_before_connection()
    {
        var store = new FakeSnapshotStore(OwnedDocument(ForeignOwner(), rows: [[8675309]]));
        var session = FakeSession.WithRows(1);

        var outcome = await Module(session, store).ExecuteAsync(Snapshot(ScopeOwner(), diff: true));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(Refusal, outcome.SafeError);
        Assert.DoesNotContain("8675309", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, session.ConnectCount);
        Assert.Empty(session.Commands);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task Scoped_diff_of_legacy_snapshot_refuses_before_connection()
    {
        var store = new FakeSnapshotStore(Document(rows: [[8675309]]));
        var session = FakeSession.WithRows(1);

        var outcome = await Module(session, store).ExecuteAsync(Snapshot(ScopeOwner(), diff: true));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(Refusal, outcome.SafeError);
        Assert.Equal(0, session.ConnectCount);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task Scoped_diff_of_own_snapshot_compares_without_cell_values()
    {
        var store = new FakeSnapshotStore(OwnedDocument(ScopeOwner(), rows: [[424242]]));

        var outcome = await Module(FakeSession.WithRows(434343), store)
            .ExecuteAsync(Snapshot(ScopeOwner(), diff: true));

        Assert.Equal(SqlHarnessExitCode.SnapshotDifferences, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessSnapshotReport>(outcome.Report);
        Assert.Equal(SnapshotVerdict.Different, report.Verdict);
        Assert.Equal(new SqlHarnessSnapshotDifference(0, 0, 0, "cell-changed"), Assert.Single(report.Differences));
        Assert.Null(outcome.SafeError);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("424242", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("434343", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_diff_of_own_identical_snapshot_succeeds()
    {
        var store = new FakeSnapshotStore(OwnedDocument(ScopeOwner(), rows: [[424242]]));

        var outcome = await Module(FakeSession.WithRows(424242), store)
            .ExecuteAsync(Snapshot(ScopeOwner(), diff: true));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(SnapshotVerdict.Identical, Assert.IsType<SqlHarnessSnapshotReport>(outcome.Report).Verdict);
        Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task Unscoped_capture_saves_without_owner()
    {
        var store = new FakeSnapshotStore();

        var outcome = await Module(FakeSession.WithRows(1), store).ExecuteAsync(Snapshot(null));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var saved = Assert.Single(store.Saves);
        Assert.Null(saved.Document.Owner);
    }

    [Fact]
    public async Task Unscoped_diff_of_legacy_snapshot_still_reads_by_name()
    {
        var store = new FakeSnapshotStore(Document(rows: [[7]]));

        var outcome = await Module(FakeSession.WithRows(7), store)
            .ExecuteAsync(Snapshot(null, diff: true));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(SnapshotVerdict.Identical, Assert.IsType<SqlHarnessSnapshotReport>(outcome.Report).Verdict);
    }

    [Fact]
    public void Saved_document_without_owner_writes_legacy_bytes()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);

        store.Save("legacy-shape", Document(rows: [[1]]), force: false);

        var json = File.ReadAllText(Path.Combine(temp.Path, "legacy-shape.json"));
        Assert.DoesNotContain("\"owner\"", json, StringComparison.Ordinal);
        Assert.Null(store.Load("legacy-shape").Owner);
    }

    [Fact]
    public void Saved_document_with_owner_round_trips_owner_shape()
    {
        using var temp = new TempDirectory();
        var store = new SnapshotStore(temp.Path);
        var owner = ScopeOwner();

        store.Save("owned-shape", OwnedDocument(owner, rows: [[1]]), force: false);

        var json = File.ReadAllText(Path.Combine(temp.Path, "owned-shape.json"));
        Assert.Contains("\"owner\"", json, StringComparison.Ordinal);
        Assert.Contains("\"profile\": \"test\"", json, StringComparison.Ordinal);
        Assert.Contains("\"engine\": \"sqlserver\"", json, StringComparison.Ordinal);
        var loaded = store.Load("owned-shape");
        Assert.True(owner.Matches(loaded.Owner));
    }

    private static SqlHarnessModule Module(FakeSession session, ISnapshotStore store) =>
        new(
            new FakeSessionFactory(session),
            new FakeGainStore(),
            () => Profiles(),
            watchClock: null,
            snapshotStore: store);

    private static SqlHarnessSnapshotOperation Snapshot(
        ArtifactOwner? owner,
        bool diff = false,
        bool force = false) =>
        new(
            Target(),
            "SELECT 1 AS Value",
            [],
            TimeoutSeconds: 30,
            MaxRows: 50,
            Name: "scope-snap",
            Diff: diff,
            Force: force,
            Owner: owner);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static SnapshotDocument Document(object?[][]? rows = null)
    {
        rows ??= [[1]];
        var columnReports = new[] { new SqlHarnessColumnReport(0, "Value", "System.Int32", AllowNull: true) };
        var scalarRows = rows
            .Select(row => (IReadOnlyList<SnapshotScalar>)row.Select(SnapshotScalar.FromValue).ToArray())
            .ToArray();
        return new SnapshotDocument(
            Version: 1,
            CreatedAt: new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
            ResultSets: [new SnapshotResultSet(columnReports, scalarRows, scalarRows.Length)],
            ResultHash: "HASH");
    }

    private static SnapshotDocument OwnedDocument(ArtifactOwner? owner, object?[][]? rows = null) =>
        Document(rows) with { Owner = owner };

    private sealed class FakeGainStore : IGainStore
    {
        public void Append(GainRecord record)
        {
        }

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSessionFactory(FakeSession session) : ISqlSessionFactory
    {
        public int ConnectCount { get; private set; }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectCount++;
            session.ConnectCount++;
            return Task.FromResult<ISqlSession>(session);
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
            if (_document is null)
                throw new FileNotFoundException($"Snapshot '{name}' was not found.");
            return _document;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sqlharness-snapshot-scope-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch { /* best effort */ }
        }
    }
}