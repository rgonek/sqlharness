using System.Globalization;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard.Transcripts;

internal sealed record TranscriptStatus(bool Enabled, int Matched, int Unmatched, int ReadErrors, string? LastCycleAt);

/// <summary>
/// Opt-in, bounded, incremental matching of journal operations to local agent transcripts.
/// Transcript files are read before one short journal write transaction; disabled cycles do no I/O.
/// </summary>
internal sealed class TranscriptMatcher(string databasePath, AgentHomes homes, TimeProvider time)
{
    internal const long CycleBudgetBytes = 32L * 1024 * 1024;
    private const int MaxPendingCodexTitleBackfills = 500;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private static readonly TimeSpan MtimeSlack = TimeSpan.FromDays(1);
    private static readonly TimeSpan StagingLifetime = TimeSpan.FromDays(7);

    private readonly object _gate = new();
    private readonly HashSet<(string Kind, string Id)> _codexTitleBackfillPending = [];
    private readonly HashSet<(string Kind, string Id)> _codexTitleBackfillAttempted = [];
    private bool _restartCodexTitleBackfill;
    private int _readErrors;
    private string? _lastCycleAt;

    internal Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;
    internal int LastCodexLookupIdentityCount { get; private set; }

    /// <summary>Opens an existing journal for the host's shared idle observer and matcher transactions.</summary>
    internal SqliteConnection? OpenJournalConnection() => Open();

    internal IReadOnlyCollection<long> PendingOperationIds(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.id FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE (s.agent_kind = 'claude' AND o.client_call_id IS NOT NULL
                     AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id))
               OR (s.agent_kind IN ('codex', 'opencode') AND o.agent_session_id IS NOT NULL
                     AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id))
            ORDER BY o.id LIMIT 500;
            """;
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    internal void RunCycle(bool enabled, SqliteConnection? journalConnection = null)
        => Run(enabled, journalConnection, operationIds: null, CancellationToken.None);

    internal Task RunTargetedAsync(IReadOnlyCollection<long> operationIds, CancellationToken ct)
        => RunTargetedAsync(operationIds, ct, journalConnection: null);

    internal Task RunTargetedAsync(IReadOnlyCollection<long> operationIds, CancellationToken ct, SqliteConnection? journalConnection)
    {
        ArgumentNullException.ThrowIfNull(operationIds);
        if (operationIds.Count == 0)
            return Task.CompletedTask;

        Run(enabled: true, journalConnection, operationIds.Take(500).ToArray(), ct);
        return Task.CompletedTask;
    }

    private void Run(bool enabled, SqliteConnection? journalConnection, IReadOnlyCollection<long>? operationIds, CancellationToken ct)
    {
        if (!enabled || ct.IsCancellationRequested)
            return;

        lock (_gate)
        {
            _readErrors = 0;
            try
            {
                using var ownedConnection = journalConnection is null ? Open() : null;
                var connection = journalConnection ?? ownedConnection;
                if (connection is null || !SchemaMatches(connection))
                    return;

                var batch = new Batch();
                AddCodexIdentities(connection, batch, operationIds);
                var budget = ScanClaude(connection, batch, CycleBudgetBytes, operationIds);
                ScanCodex(connection, batch, budget, operationIds);
                Commit(connection, batch);
            }
            catch
            {
                _readErrors++;
            }
            finally
            {
                _lastCycleAt = Timestamp(time.GetUtcNow());
            }
        }
    }

    internal TranscriptStatus Status(bool enabled)
    {
        lock (_gate)
        {
            var matched = 0;
            var unmatched = 0;
            try
            {
                using var connection = Open();
                if (connection is not null && SchemaMatches(connection))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT
                          (SELECT COUNT(*) FROM operation_agent_links),
                          (SELECT COUNT(*) FROM operations o JOIN sessions s ON s.id = o.session_id
                           WHERE ((s.agent_kind = 'claude' AND o.client_call_id IS NOT NULL)
                               OR (s.agent_kind IN ('codex', 'opencode') AND o.agent_session_id IS NOT NULL))
                             AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id));
                        """;
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        matched = reader.GetInt32(0);
                        unmatched = reader.GetInt32(1);
                    }
                }
            }
            catch (SqliteException)
            {
                // Status is a best-effort view of the journal.
            }

            return new TranscriptStatus(enabled, matched, unmatched, _readErrors, _lastCycleAt);
        }
    }

    internal bool Clear()
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                if (connection is null || !SchemaMatches(connection))
                    return false;

                Execute(connection, "BEGIN IMMEDIATE;");
                try
                {
                    Execute(connection, "DELETE FROM operation_agent_links; DELETE FROM agent_tool_calls; DELETE FROM agent_conversations; DELETE FROM transcript_cursors;");
                    Execute(connection, "COMMIT;");
                    return true;
                }
                catch
                {
                    Execute(connection, "ROLLBACK;");
                    throw;
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteBusy or SqliteLocked)
            {
                return false;
            }
            catch (SqliteException)
            {
                return false;
            }
        }
    }

    private SqliteConnection? Open()
    {
        if (!File.Exists(databasePath))
            return null;

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool SchemaMatches(SqliteConnection connection) =>
        Convert.ToInt64(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture) == JournalSchema.CurrentVersion;

    private long ScanClaude(SqliteConnection connection, Batch batch, long budget, IReadOnlyCollection<long>? operationIds)
    {
        using var command = connection.CreateCommand();
        var idFilter = operationIds is null ? string.Empty : " AND o.id IN (" + string.Join(",", operationIds.Select((_, i) => "$id" + i)) + ")";
        command.CommandText = """
            SELECT s.cwd, MIN(o.started_at) FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE s.agent_kind = 'claude' AND o.client_call_id IS NOT NULL AND s.cwd IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id)
            """ + idFilter + " GROUP BY s.cwd;";
        if (operationIds is not null)
            for (var i = 0; i < operationIds.Count; i++)
                command.Parameters.AddWithValue("$id" + i, operationIds.ElementAt(i));
        var pending = new List<(string Cwd, DateTimeOffset Oldest)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var oldest))
                    pending.Add((reader.GetString(0), oldest));
            }
        }

        foreach (var (cwd, oldest) in pending)
        {
            if (budget <= 0)
                break;
            var directory = homes.ClaudeProjectDirectory(cwd);
            if (!DirectoryExists(directory))
                continue;

            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
                {
                    if (budget <= 0)
                        break;
                    if (HasMemorySegment(path))
                        continue;

                    try
                    {
                        var info = new FileInfo(path);
                        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                        if (mtime < oldest - MtimeSlack)
                            continue;

                        var result = IncrementalTail.Read(path, LoadCursor(connection, path), budget,
                            line => Collect(batch, TranscriptLines.ParseClaude(line)));
                        batch.Cursors[path] = result.Cursor;
                        budget -= result.BytesRead;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _readErrors++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _readErrors++;
            }
        }

        return budget;
    }

    private long ScanCodex(SqliteConnection connection, Batch batch, long budget, IReadOnlyCollection<long>? operationIds)
    {
        using var command = connection.CreateCommand();
        var idFilter = operationIds is null ? string.Empty : " AND o.id IN (" + string.Join(",", operationIds.Select((_, i) => "$id" + i)) + ")";
        command.CommandText = """
            SELECT DISTINCT s.agent_kind, o.agent_session_id FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE s.agent_kind IN ('codex', 'opencode') AND o.agent_session_id IS NOT NULL
            """ + idFilter + ";";
        if (operationIds is not null)
        {
            for (var i = 0; i < operationIds.Count; i++)
                command.Parameters.AddWithValue("$id" + i, operationIds.ElementAt(i));
        }
        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                AddWanted(reader.GetString(1), reader.GetString(0));
        }

        foreach (var key in batch.Conversations.Keys.Where(key => key.Kind is "codex" or "opencode"))
            AddWanted(key.Id, key.Kind);

        var fullSweep = operationIds is null;
        var existingNullTitles = fullSweep ? NullTitleConversationIds(connection).ToHashSet() : [];
        if (fullSweep)
        {
            _codexTitleBackfillAttempted.RemoveWhere(key => !existingNullTitles.Contains(key));
            var unresolvedTitles = existingNullTitles.Concat(batch.Conversations
                    .Where(pair => pair.Key.Kind is "codex" or "opencode" && pair.Value is null)
                    .Select(pair => pair.Key))
                .ToHashSet();
            _codexTitleBackfillPending.RemoveWhere(key => !unresolvedTitles.Contains(key));
        }
        foreach (var key in batch.Conversations.Where(pair => pair.Key.Kind is "codex" or "opencode" && pair.Value is not null).Select(pair => pair.Key))
        {
            _codexTitleBackfillAttempted.Remove(key);
            _codexTitleBackfillPending.Remove(key);
        }

        var missingTitles = (operationIds is null ? existingNullTitles : [])
            .Concat(batch.Conversations
                .Where(pair => pair.Key.Kind is "codex" or "opencode" && pair.Value is null)
                .Select(pair => pair.Key))
            .Distinct()
            .Where(key => !_codexTitleBackfillAttempted.Contains(key))
            .ToArray();
        foreach (var key in missingTitles)
        {
            if (_codexTitleBackfillPending.Count < MaxPendingCodexTitleBackfills && _codexTitleBackfillPending.Add(key))
                _restartCodexTitleBackfill = true;
        }

        foreach (var key in fullSweep
                     ? _codexTitleBackfillPending.Concat(_codexTitleBackfillAttempted)
                     : _codexTitleBackfillPending)
            AddWanted(key.Id, key.Kind);

        LastCodexLookupIdentityCount = wanted.Count;

        if (wanted.Count == 0 || budget <= 0 || !File.Exists(homes.CodexSessionIndex))
            return budget;

        var path = homes.CodexSessionIndex;
        try
        {
            var cursor = _restartCodexTitleBackfill ? null : LoadCursor(connection, path);
            _restartCodexTitleBackfill = false;
            var result = IncrementalTail.Read(path, cursor, budget, line =>
            {
                var entry = TranscriptLines.ParseCodexIndex(line);
                if (entry is not null && wanted.TryGetValue(entry.Id, out var kinds))
                {
                    foreach (var kind in kinds)
                        batch.AddConversation(kind, entry.Id, entry.Title);
                }
            });
            if (result.Completed && _codexTitleBackfillPending.Count > 0)
            {
                foreach (var key in _codexTitleBackfillPending)
                {
                    if (batch.Conversations.TryGetValue(key, out var title) && title is not null)
                        _codexTitleBackfillAttempted.Remove(key);
                    else
                        _codexTitleBackfillAttempted.Add(key);
                }
                _codexTitleBackfillPending.Clear();
            }
            batch.Cursors[path] = result.Cursor;
            return budget - result.BytesRead;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _readErrors++;
            return budget;
        }

        void AddWanted(string id, string kind)
        {
            if (!wanted.TryGetValue(id, out var kinds))
                wanted[id] = kinds = new HashSet<string>(StringComparer.Ordinal);
            kinds.Add(kind);
        }
    }

    private static void AddCodexIdentities(SqliteConnection connection, Batch batch, IReadOnlyCollection<long>? operationIds)
    {
        using var command = connection.CreateCommand();
        var idFilter = operationIds is null ? string.Empty : " AND o.id IN (" + string.Join(",", operationIds.Select((_, i) => "$id" + i)) + ")";
        command.CommandText = "SELECT DISTINCT s.agent_kind, o.agent_session_id FROM operations o JOIN sessions s ON s.id = o.session_id WHERE s.agent_kind IN ('codex', 'opencode') AND o.agent_session_id IS NOT NULL" + idFilter + ";";
        if (operationIds is not null)
            for (var i = 0; i < operationIds.Count; i++)
                command.Parameters.AddWithValue("$id" + i, operationIds.ElementAt(i));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = reader.GetString(0);
            var id = reader.GetString(1);
            batch.AddConversation(kind, id, null);
        }
    }

    private static IEnumerable<(string Kind, string Id)> NullTitleConversationIds(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_kind, conversation_id FROM agent_conversations WHERE agent_kind IN ('codex', 'opencode') AND title IS NULL;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return (reader.GetString(0), reader.GetString(1));
    }

    private static void Collect(Batch batch, ClaudeLine? line)
    {
        if (line is null)
            return;
        if (line.AiTitle is not null)
            batch.AddConversation("claude", line.ConversationId, line.AiTitle);
        foreach (var callId in line.SqlHarnessToolUseIds)
        {
            batch.AddConversation("claude", line.ConversationId, null);
            batch.ToolCalls.Add((callId, line.ConversationId, line.Model, line.Effort));
        }
    }

    private void Commit(SqliteConnection connection, Batch batch)
    {
        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            var now = Timestamp(time.GetUtcNow());
            foreach (var (key, title) in batch.Conversations)
                Execute(connection, """
                    INSERT INTO agent_conversations (agent_kind, conversation_id, title, updated_at) VALUES ($kind, $id, $title, $now)
                    ON CONFLICT(agent_kind, conversation_id) DO UPDATE SET title = COALESCE(excluded.title, title), updated_at = $now;
                    """, ("$kind", key.Kind), ("$id", key.Id), ("$title", title), ("$now", now));

            foreach (var (callId, conversationId, model, effort) in batch.ToolCalls)
                Execute(connection, """
                    INSERT INTO agent_tool_calls (agent_kind, call_id, conversation, model, effort, seen_at)
                    SELECT 'claude', $call, c.id, $model, $effort, $now FROM agent_conversations c
                    WHERE c.agent_kind = 'claude' AND c.conversation_id = $conv
                    ON CONFLICT(agent_kind, call_id) DO NOTHING;
                    """, ("$call", callId), ("$model", model), ("$effort", effort), ("$now", now), ("$conv", conversationId));

            foreach (var (path, cursor) in batch.Cursors)
                Execute(connection, """
                    INSERT INTO transcript_cursors (path, size, mtime, offset) VALUES ($path, $size, $mtime, $offset)
                    ON CONFLICT(path) DO UPDATE SET size = excluded.size, mtime = excluded.mtime, offset = excluded.offset;
                    """, ("$path", path), ("$size", cursor.Size), ("$mtime", cursor.Mtime.ToString("O", CultureInfo.InvariantCulture)), ("$offset", cursor.Offset));

            Execute(connection, """
                INSERT INTO operation_agent_links (operation_id, conversation, model, effort)
                SELECT o.id, t.conversation, t.model, t.effort
                FROM operations o JOIN sessions s ON s.id = o.session_id
                JOIN agent_tool_calls t ON t.agent_kind = 'claude' AND t.call_id = o.client_call_id
                WHERE s.agent_kind = 'claude' AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id);
                INSERT INTO operation_agent_links (operation_id, conversation)
                SELECT o.id, c.id
                FROM operations o JOIN sessions s ON s.id = o.session_id
                JOIN agent_conversations c ON c.agent_kind = s.agent_kind AND c.conversation_id = o.agent_session_id
                WHERE s.agent_kind IN ('codex', 'opencode')
                  AND NOT EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id);
                DELETE FROM agent_tool_calls WHERE agent_kind = 'claude' AND (
                    EXISTS (SELECT 1 FROM operations o JOIN sessions s ON s.id = o.session_id
                            JOIN operation_agent_links l ON l.operation_id = o.id
                            WHERE s.agent_kind = 'claude' AND o.client_call_id = agent_tool_calls.call_id)
                    OR seen_at < $stagingCutoff);
                """, ("$stagingCutoff", Timestamp(time.GetUtcNow() - StagingLifetime)));
            Execute(connection, "COMMIT;");
        }
        catch
        {
            Execute(connection, "ROLLBACK;");
            throw;
        }
    }

    private static TailCursor? LoadCursor(SqliteConnection connection, string path)
    {
        using var command = Command(connection, "SELECT size, mtime, offset FROM transcript_cursors WHERE path = $path;", ("$path", path));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new TailCursor(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetInt64(2))
            : null;
    }

    private static bool HasMemorySegment(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(segment => string.Equals(segment, "memory", StringComparison.OrdinalIgnoreCase));

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private sealed class Batch
    {
        internal Dictionary<(string Kind, string Id), string?> Conversations { get; } = new();
        internal List<(string CallId, string Conversation, string? Model, string? Effort)> ToolCalls { get; } = [];
        internal Dictionary<string, TailCursor> Cursors { get; } = new(StringComparer.Ordinal);

        internal void AddConversation(string kind, string id, string? title)
        {
            var key = (kind, id);
            if (title is not null || !Conversations.ContainsKey(key))
                Conversations[key] = title;
        }
    }
}