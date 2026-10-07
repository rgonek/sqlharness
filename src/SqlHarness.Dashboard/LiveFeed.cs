using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace SqlHarness.Dashboard;

/// <summary>
/// Server-sent events over the journal. One read-only connection stays open for
/// the stream because PRAGMA data_version only changes for commits made by other
/// connections; rows are queried only when it changes. Running rows are re-checked
/// each tick so a dead process turns into an <c>abandoned</c> event without any write.
/// Each tick reads one WAL snapshot.
/// </summary>
internal static class LiveFeed
{
    internal const int BatchLimit = 500;
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task StreamAsync(HttpContext context, JournalReader reader, TimeSpan interval, CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        if (HttpMethods.IsHead(context.Request.Method))
            return;

        using var feed = new Feed(reader);
        try
        {
            // The baseline is taken before the client hears "connected", so every change
            // the client can cause after that point is news.
            feed.TryOpen();
            await Comment(context, "connected", ct);
            var lastBeat = DateTimeOffset.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                foreach (var (name, data) in feed.Poll())
                    await Send(context, name, data, ct);

                if (DateTimeOffset.UtcNow - lastBeat >= Heartbeat)
                {
                    lastBeat = DateTimeOffset.UtcNow;
                    await Comment(context, "ping", ct);
                }

                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or the server is stopping.
        }
        catch (IOException)
        {
            // The client went away mid-write.
        }
    }

    private sealed class Feed(JournalReader reader) : IDisposable
    {
        private readonly HashSet<(long, string)> _sentAtOperationCursor = [];
        private readonly HashSet<(long, string)> _sentAtSessionCursor = [];
        private readonly Dictionary<long, string> _runningStatus = [];
        private SqliteConnection? _connection;
        private string? _operationCursor;
        private string? _sessionCursor;
        private long? _lastVersion;
        private bool _initialized;
        private bool _sawMissingDatabase;

        /// <summary>
        /// Opens the connection; the first successful open sets the cursors. A database
        /// that was missing or unreadable at connect streams everything once it appears;
        /// an existing one streams only news, so rows at its newest timestamp count as sent.
        /// </summary>
        public bool TryOpen()
        {
            if (_connection is not null)
                return true;
            try
            {
                _connection = reader.OpenReadOnly();
                if (_connection is null)
                {
                    _sawMissingDatabase = true;
                    return false;
                }

                if (!_initialized)
                {
                    InSnapshot(Initialize);
                    _initialized = true;
                }

                return true;
            }
            catch (SqliteException)
            {
                // A database still being created or migrated: treat it like a missing one and retry.
                _sawMissingDatabase = true;
                Reset();
                return false;
            }
        }

        public List<(string Name, string Data)> Poll()
        {
            var events = new List<(string, string)>();
            if (!TryOpen())
                return events;
            try
            {
                InSnapshot(() => Read(events));
            }
            catch (SqliteException)
            {
                // Retry on the next tick with a fresh connection; the cursors are kept.
                Reset();
            }

            return events;
        }

        public void Dispose() => _connection?.Dispose();

        private void Initialize()
        {
            var existing = _sawMissingDatabase ? null : JournalReader.MaxTimestamp(_connection!);
            _operationCursor = existing ?? string.Empty;
            _sessionCursor = existing ?? string.Empty;
            if (existing is not null)
            {
                foreach (var session in reader.SessionsSeenSince(_connection!, existing, BatchLimit))
                    Advance(ref _sessionCursor, _sentAtSessionCursor, session.Id, session.LastSeen);
                foreach (var operation in reader.OperationsUpdatedSince(_connection!, existing, BatchLimit))
                    Advance(ref _operationCursor, _sentAtOperationCursor, operation.Id, operation.UpdatedAt);
            }

            foreach (var running in reader.RunningOperations(_connection!))
                _runningStatus[running.Id] = running.Status;
        }

        private void Read(List<(string, string)> events)
        {
            var connection = _connection!;
            var version = DataVersion(connection);
            if (version != _lastVersion)
            {
                var sessions = reader.SessionsSeenSince(connection, _sessionCursor!, BatchLimit);
                foreach (var session in sessions)
                {
                    if (Advance(ref _sessionCursor, _sentAtSessionCursor, session.Id, session.LastSeen))
                        events.Add(("session", JsonSerializer.Serialize(session, Json)));
                }

                var operations = reader.OperationsUpdatedSince(connection, _operationCursor!, BatchLimit);
                foreach (var operation in operations)
                {
                    if (!Advance(ref _operationCursor, _sentAtOperationCursor, operation.Id, operation.UpdatedAt))
                        continue;
                    if (operation.Status is "running" or "abandoned")
                        _runningStatus[operation.Id] = operation.Status;
                    else
                        _runningStatus.Remove(operation.Id);
                    events.Add(("operation", JsonSerializer.Serialize(operation, Json)));
                }

                // A full batch may have left rows behind: query again next tick even without a new commit.
                _lastVersion = sessions.Count < BatchLimit && operations.Count < BatchLimit ? version : null;
            }

            // Liveness changes produce no journal write, so check running rows every tick.
            foreach (var running in reader.RunningOperations(connection))
            {
                if (_runningStatus.TryGetValue(running.Id, out var known) && known == running.Status)
                    continue;
                _runningStatus[running.Id] = running.Status;
                events.Add(("operation", JsonSerializer.Serialize(running, Json)));
            }
        }

        private void InSnapshot(Action read)
        {
            Execute(_connection!, "BEGIN DEFERRED;");
            try
            {
                read();
            }
            finally
            {
                Execute(_connection!, "COMMIT;");
            }
        }

        private void Reset()
        {
            _connection?.Dispose();
            _connection = null;
            _lastVersion = null;
        }
    }

    /// <summary>
    /// Keyset cursor on (timestamp, id): rows with the same millisecond timestamp as the
    /// cursor are de-duplicated by id, so equal timestamps are neither lost nor repeated.
    /// </summary>
    private static bool Advance(ref string? cursor, HashSet<(long, string)> sentAtCursor, long id, string timestamp)
    {
        var comparison = string.CompareOrdinal(timestamp, cursor);
        if (comparison < 0)
            return false;
        if (comparison > 0)
        {
            cursor = timestamp;
            sentAtCursor.Clear();
        }

        return sentAtCursor.Add((id, timestamp));
    }

    private static long DataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task Send(HttpContext context, string name, string data, CancellationToken ct)
    {
        await context.Response.WriteAsync($"event: {name}\ndata: {data}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static async Task Comment(HttpContext context, string text, CancellationToken ct)
    {
        await context.Response.WriteAsync($": {text}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
}