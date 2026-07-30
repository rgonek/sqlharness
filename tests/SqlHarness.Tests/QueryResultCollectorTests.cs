using System.Data;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class QueryResultCollectorTests
{
    [Fact]
    public async Task Collects_multiple_result_sets_with_truncation_redaction_and_canonical_hash()
    {
        var messages = new List<string>();
        var reader = FakeCollectorReader.FromSets(
            [
                new ResultSet(["Value"], [1], [2], [3]),
                new ResultSet(["Other"], ["a"]),
            ],
            onSuccessfulRead: () =>
            {
                if (messages.Count == 0)
                    messages.Add("contains secret token");
            });
        var session = new FakeCollectorSession(reader, messages);
        using var raw = new CanonicalResultAccumulator();

        var result = await QueryResultCollector.CollectAsync(
            session, new SqlExecutionCommand("select", [], 30), maxRows: 2,
            knownSecrets: ["secret"], () => raw, CancellationToken.None);

        Assert.Equal(2, result.ResultSets.Count);
        Assert.Equal(3, result.ResultSets[0].RowCount);
        Assert.Equal(1, result.ResultSets[0].OmittedRowCount);
        Assert.Equal(2, result.ResultSets[0].Rows.Count);
        // Global maxRows=2 already spent on the first set, so the second row is omitted.
        Assert.Equal(1, result.ResultSets[1].RowCount);
        Assert.Equal(1, result.ResultSets[1].OmittedRowCount);
        Assert.Empty(result.ResultSets[1].Rows);
        Assert.DoesNotContain("secret", string.Join('\n', result.Messages));
        Assert.Contains("[REDACTED]", string.Join('\n', result.Messages), StringComparison.Ordinal);
        Assert.NotEmpty(result.Canonical.Hash);
        Assert.Equal(-1, result.RecordsAffected);
        Assert.Equal("select", Assert.Single(session.Commands).Sql);
    }

    [Fact]
    public async Task Zero_column_result_is_skipped_while_messages_are_retained()
    {
        var messages = new List<string>();
        var reader = FakeCollectorReader.FromSets(
            [
                ResultSet.ZeroColumn(),
                new ResultSet(["Value"], [42]),
            ],
            onNextResult: () =>
            {
                if (messages.Count == 0)
                    messages.Add("print from zero-column batch");
            });
        var session = new FakeCollectorSession(reader, messages);
        using var raw = new CanonicalResultAccumulator();

        var result = await QueryResultCollector.CollectAsync(
            session, new SqlExecutionCommand("select", [], 30), maxRows: 50,
            knownSecrets: [], () => raw, CancellationToken.None);

        Assert.Single(result.ResultSets);
        Assert.Equal(1, result.ResultSets[0].RowCount);
        Assert.Equal(["print from zero-column batch"], result.Messages);
    }

    [Fact]
    public async Task Multiple_result_sets_are_all_reported_and_hashed()
    {
        var reader = FakeCollectorReader.FromSets(
        [
            new ResultSet(["A"], [1]),
            new ResultSet(["B"], [2]),
            new ResultSet(["C"], [3]),
        ]);
        var session = new FakeCollectorSession(reader);
        using var raw = new CanonicalResultAccumulator();

        var result = await QueryResultCollector.CollectAsync(
            session, new SqlExecutionCommand("select", [], 30), maxRows: 50,
            knownSecrets: [], () => raw, CancellationToken.None);

        Assert.Equal(3, result.ResultSets.Count);
        Assert.Equal([1], result.ResultSets[0].Rows[0]);
        Assert.Equal([2], result.ResultSets[1].Rows[0]);
        Assert.Equal([3], result.ResultSets[2].Rows[0]);
        Assert.NotEmpty(result.Canonical.Hash);
    }

    [Fact]
    public async Task Row_truncation_is_global_across_result_sets_and_still_drains_all_rows()
    {
        var reader = FakeCollectorReader.FromSets(
        [
            new ResultSet(["Value"], [1], [2], [3]),
            new ResultSet(["Value"], [4], [5]),
        ]);
        var session = new FakeCollectorSession(reader);
        using var raw = new CanonicalResultAccumulator();

        var result = await QueryResultCollector.CollectAsync(
            session, new SqlExecutionCommand("select", [], 30), maxRows: 2,
            knownSecrets: [], () => raw, CancellationToken.None);

        Assert.Equal(2, result.ResultSets[0].Rows.Count);
        Assert.Equal(1, result.ResultSets[0].OmittedRowCount);
        Assert.Empty(result.ResultSets[1].Rows);
        Assert.Equal(2, result.ResultSets[1].OmittedRowCount);
        Assert.Equal(3, result.ResultSets[0].RowCount);
        Assert.Equal(2, result.ResultSets[1].RowCount);
        // 3 rows + terminal false, then 2 rows + terminal false.
        Assert.Equal(7, reader.ReadCalls);
    }

    [Fact]
    public async Task Unsupported_scalar_fails_after_partial_raw_messages_are_appended()
    {
        const string message = "safe diagnostic before scalar failure";
        var messages = new List<string>();
        var reader = FakeCollectorReader.FromSets(
            [new ResultSet(["Value"], [float.PositiveInfinity])],
            onSuccessfulRead: () => messages.Add(message));
        var session = new FakeCollectorSession(reader, messages);
        using var raw = new CanonicalResultAccumulator();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            QueryResultCollector.CollectAsync(
                session, new SqlExecutionCommand("select", [], 30), maxRows: 50,
                knownSecrets: [], () => raw, CancellationToken.None));

        Assert.Contains("Non-finite", ex.Message, StringComparison.Ordinal);
        // Failure path ends the open raw result set then appends safe messages.
        Assert.True(raw.SnapshotFootprint().Bytes > 0);
        Assert.True(raw.SnapshotFootprint().Lines >= 1);
    }

    [Fact]
    public async Task Cancellation_during_read_propagates_and_appends_partial_messages()
    {
        using var cts = new CancellationTokenSource();
        var messages = new List<string>();
        var reader = FakeCollectorReader.FromSets(
            [new ResultSet(["Value"], [1], [2])],
            cancelAfterSuccessfulReads: 1,
            onCancel: () =>
            {
                messages.Add("before cancel");
                cts.Cancel();
            });
        var session = new FakeCollectorSession(reader, messages);
        using var raw = new CanonicalResultAccumulator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            QueryResultCollector.CollectAsync(
                session, new SqlExecutionCommand("select", [], 30), maxRows: 50,
                knownSecrets: [], () => raw, cts.Token));

        Assert.True(raw.SnapshotFootprint().Bytes > 0);
    }

    [Fact]
    public async Task Message_redaction_applies_only_to_messages_from_this_execution()
    {
        var messages = new List<string> { "earlier identity message with secret" };
        var reader = FakeCollectorReader.FromSets(
            [new ResultSet(["Value"], [1])],
            onSuccessfulRead: () => messages.Add("late message with secret"));
        var session = new FakeCollectorSession(reader, messages);
        using var raw = new CanonicalResultAccumulator();

        var result = await QueryResultCollector.CollectAsync(
            session, new SqlExecutionCommand("select", [], 30), maxRows: 50,
            knownSecrets: ["secret"], () => raw, CancellationToken.None);

        Assert.Equal(["late message with [REDACTED]"], result.Messages);
        Assert.DoesNotContain("secret", string.Join('\n', result.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("earlier identity", string.Join('\n', result.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Captures_messages_emitted_during_read_in_canonical_and_raw()
    {
        const string message = "late message from ReadAsync";
        var messages = new List<string> { "earlier identity message" };
        var reader = FakeCollectorReader.FromSets(
            [new ResultSet(["Value"], [1])],
            onSuccessfulRead: () => messages.Add(message));
        var session = new FakeCollectorSession(reader, messages);
        using var raw = new CanonicalResultAccumulator();

        var result = await QueryResultCollector.CollectAsync(
            session, new SqlExecutionCommand("select", [], 30), maxRows: 50,
            knownSecrets: [], () => raw, CancellationToken.None);

        Assert.Equal([message], result.Messages);
        using var expected = new CanonicalResultAccumulator();
        expected.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        expected.AddRow([1]);
        expected.EndResultSet();
        expected.AddMessage("sql", message);
        var canonical = expected.Complete();
        Assert.Equal(canonical.Hash, result.Canonical.Hash);
        Assert.Equal(canonical.Footprint, raw.Complete().Footprint);
    }

    [Fact]
    public async Task Records_affected_comes_from_the_reader()
    {
        var reader = FakeCollectorReader.FromSets(
            [new ResultSet(["Value"], [1])],
            recordsAffected: 7);
        var session = new FakeCollectorSession(reader);
        using var raw = new CanonicalResultAccumulator();

        var result = await QueryResultCollector.CollectAsync(
            session, new SqlExecutionCommand("select", [], 30), maxRows: 50,
            knownSecrets: [], () => raw, CancellationToken.None);

        Assert.Equal(7, result.RecordsAffected);
    }

    [Fact]
    public async Task Open_failure_does_not_invoke_createRaw()
    {
        var session = FakeCollectorSession.FailOpen(new TimeoutException("open failed"));
        var createRawCalls = 0;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            QueryResultCollector.CollectAsync(
                session, new SqlExecutionCommand("select", [], 30), maxRows: 50,
                knownSecrets: [],
                () =>
                {
                    createRawCalls++;
                    return new CanonicalResultAccumulator();
                },
                CancellationToken.None));

        Assert.Equal(0, createRawCalls);
        Assert.Equal("select", Assert.Single(session.Commands).Sql);
    }

    private sealed record ResultSet(string[] Names, params object?[][] Rows)
    {
        public static ResultSet ZeroColumn() => new([], Array.Empty<object?[]>());
    }

    private sealed class FakeCollectorSession : ISqlSession
    {
        private readonly ISqlReader? _reader;
        private readonly Exception? _openFailure;
        private readonly IReadOnlyList<string> _messages;

        public FakeCollectorSession(ISqlReader reader, IReadOnlyList<string>? messages = null)
        {
            _reader = reader;
            _messages = messages ?? [];
        }

        private FakeCollectorSession(Exception openFailure)
        {
            _openFailure = openFailure;
            _messages = [];
        }

        public static FakeCollectorSession FailOpen(Exception failure) => new(failure);

        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages =>
            _messages is List<string> list ? list.ToArray() : _messages.ToArray();
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "db", "server", "db", "profile");

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (_openFailure is not null)
                return Task.FromException<ISqlReader>(_openFailure);
            return Task.FromResult(_reader!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCollectorReader : ISqlReader
    {
        private readonly ResultSet[] _sets;
        private readonly int? _cancelAfterSuccessfulReads;
        private readonly Action? _onCancel;
        private readonly Action? _onSuccessfulRead;
        private readonly Action? _onNextResult;
        private int _set;
        private int _position = -1;
        private int _successfulReads;

        private FakeCollectorReader(
            ResultSet[] sets,
            int recordsAffected,
            int? cancelAfterSuccessfulReads,
            Action? onCancel,
            Action? onSuccessfulRead,
            Action? onNextResult)
        {
            _sets = sets;
            RecordsAffected = recordsAffected;
            _cancelAfterSuccessfulReads = cancelAfterSuccessfulReads;
            _onCancel = onCancel;
            _onSuccessfulRead = onSuccessfulRead;
            _onNextResult = onNextResult;
        }

        public static FakeCollectorReader FromSets(
            ResultSet[] sets,
            int recordsAffected = -1,
            int? cancelAfterSuccessfulReads = null,
            Action? onCancel = null,
            Action? onSuccessfulRead = null,
            Action? onNextResult = null) =>
            new(sets, recordsAffected, cancelAfterSuccessfulReads, onCancel, onSuccessfulRead, onNextResult);

        public int ReadCalls { get; private set; }
        public int FieldCount => _sets[_set].Names.Length;
        public int RecordsAffected { get; }
        public string GetName(int ordinal) => _sets[_set].Names[ordinal];
        public Type GetFieldType(int ordinal) =>
            _sets[_set].Rows.FirstOrDefault()?[ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) =>
            _sets[_set].Rows.Any(row => row[ordinal] is null or DBNull);
        public object GetValue(int ordinal) =>
            _sets[_set].Rows[_position][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            ReadCalls++;
            ct.ThrowIfCancellationRequested();
            var rows = _sets[_set].Rows;
            var hasRow = ++_position < rows.Length;
            if (hasRow)
            {
                _successfulReads++;
                _onSuccessfulRead?.Invoke();
                if (_cancelAfterSuccessfulReads is int limit && _successfulReads >= limit)
                {
                    _onCancel?.Invoke();
                    ct.ThrowIfCancellationRequested();
                }
            }

            return Task.FromResult(hasRow);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++_set < _sets.Length)
            {
                _position = -1;
                _onNextResult?.Invoke();
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
