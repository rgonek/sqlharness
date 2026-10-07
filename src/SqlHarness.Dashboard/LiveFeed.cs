using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace SqlHarness.Dashboard;

/// <summary>
/// Server-sent events over the journal. One read-only connection stays open for
/// the stream because PRAGMA data_version only changes for commits made by other
/// connections; rows are queried only when it changes. Running rows are re-checked
/// each tick so a dead process turns into an <c>abandoned</c> event without any write.
/// Each tick reads one WAL snapshot, and a row is sent whenever its payload changes.
/// </summary>
internal static class LiveFeed
{
    internal const int BatchLimit = 500;

    /// <summary>How far behind the newest timestamp a late commit is still picked up by the window re-read.</summary>
    internal static readonly TimeSpan Lookback = TimeSpan.FromSeconds(10);
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
        private readonly Dictionary<long, Sent> _operations = [];
        private readonly Dictionary<long, Sent> _sessions = [];
        private readonly HashSet<long> _live = [];
        private readonly HashSet<long> _touchedSessions = [];
        private SqliteConnection? _connection;
        private string _operationCursor = string.Empty;
        private string _sessionCursor = string.Empty;
        private long? _lastVersion;
        private bool _initialized;
        private bool _sawMissingDatabase;

        /// <summary>
        /// Opens the connection; the first successful open sets the baseline. A database
        /// that was missing or unreadable at connect streams everything once it appears;
        /// an existing one streams only news, so the rows it already holds count as sent.
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
                    if (!_sawMissingDatabase)
                    {
                        InSnapshot(() =>
                        {
                            _operationCursor = _sessionCursor = JournalReader.MaxTimestamp(_connection) ?? string.Empty;
                            Read(null);
                        });
                    }

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
                // Retry on the next tick with a fresh connection; what was sent is kept.
                Reset();
            }

            return events;
        }

        public void Dispose() => _connection?.Dispose();

        /// <summary>
        /// One tick on one snapshot. Writers stamp a row before they take the write lock,
        /// so commit order is not timestamp order: every commit re-reads the trailing
        /// <see cref="Lookback"/> window and emits rows whose payload changed. Running rows
        /// are re-read every tick, because liveness changes produce no journal write, and a
        /// running row that left the running set is fetched by id even when its timestamp
        /// fell behind the window. Null <paramref name="events"/> records without emitting.
        /// </summary>
        private void Read(List<(string, string)>? events)
        {
            var connection = _connection!;
            var version = DataVersion(connection);
            if (version != _lastVersion)
            {
                _lastVersion = version;
                var sessionFloor = Floor(_sessionCursor);
                foreach (var session in Pages(sessionFloor, (at, id) => reader.SessionsSeenAfter(connection, at, id, BatchLimit), s => (s.LastSeen, s.Id)))
                    Session(session, events);

                var operationFloor = Floor(_operationCursor);
                foreach (var operation in Pages(operationFloor, (at, id) => reader.OperationsUpdatedAfter(connection, at, id, BatchLimit), o => (o.UpdatedAt, o.Id)))
                    Operation(operation, events);

                Prune(_sessions, sessionFloor, null);
                Prune(_operations, operationFloor, _live);
            }

            var running = reader.RunningOperations(connection);
            foreach (var operation in running)
                Operation(operation, events);
            foreach (var id in _live.Except(running.Select(operation => operation.Id)).ToArray())
            {
                if (reader.OperationById(connection, id) is { } operation)
                    Operation(operation, events);
                else
                    _live.Remove(id);
            }

            foreach (var id in _touchedSessions)
            {
                if (reader.SessionById(connection, id) is { } session)
                    Session(session, events);
            }

            _touchedSessions.Clear();
        }

        private void Operation(OperationSummary operation, List<(string, string)>? events)
        {
            var json = JsonSerializer.Serialize(operation, Json);
            var known = _operations.TryGetValue(operation.Id, out var sent);
            if (known && sent.Json == json)
                return;
            _operations[operation.Id] = new Sent(operation.UpdatedAt, json, operation.Status);
            if (string.CompareOrdinal(operation.UpdatedAt, _operationCursor) > 0)
                _operationCursor = operation.UpdatedAt;
            if (operation.Status is "running" or "abandoned")
                _live.Add(operation.Id);
            else
                _live.Remove(operation.Id);
            if (events is null)
                return;
            events.Add(("operation", json));
            // A new row or a status change moves its session's running and abandoned counts.
            if (!known || sent.Status != operation.Status)
                _touchedSessions.Add(operation.SessionId);
        }

        private void Session(SessionSummary session, List<(string, string)>? events)
        {
            var json = JsonSerializer.Serialize(session, Json);
            if (_sessions.TryGetValue(session.Id, out var sent) && sent.Json == json)
                return;
            _sessions[session.Id] = new Sent(session.LastSeen, json, null);
            if (string.CompareOrdinal(session.LastSeen, _sessionCursor) > 0)
                _sessionCursor = session.LastSeen;
            events?.Add(("session", json));
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

    private readonly record struct Sent(string Timestamp, string Json, string? Status);

    /// <summary>Every row from <paramref name="floor"/> on, as keyset pages on (timestamp, id) so each page progresses.</summary>
    private static IEnumerable<T> Pages<T>(string floor, Func<string, long, IReadOnlyList<T>> page, Func<T, (string At, long Id)> key)
    {
        var (at, id) = (floor, 0L);
        while (true)
        {
            var rows = page(at, id);
            foreach (var row in rows)
                yield return row;
            if (rows.Count < BatchLimit)
                yield break;
            (at, id) = key(rows[^1]);
        }
    }

    /// <summary>Start of the re-read window: <see cref="Lookback"/> before the newest timestamp seen.</summary>
    private static string Floor(string cursor) =>
        DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var newest)
            ? (newest - Lookback).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : cursor;

    /// <summary>Forgets rows that left the window so memory stays bounded; <paramref name="keep"/> ids stay tracked.</summary>
    private static void Prune(Dictionary<long, Sent> sent, string floor, HashSet<long>? keep)
    {
        foreach (var (id, row) in sent.ToArray())
        {
            if (string.CompareOrdinal(row.Timestamp, floor) < 0 && keep?.Contains(id) != true)
                sent.Remove(id);
        }
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