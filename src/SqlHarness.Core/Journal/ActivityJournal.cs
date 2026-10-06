using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace SqlHarness.Core;

/// <summary>
/// SQLite activity journal (WAL). Every public write is best-effort: failures
/// are swallowed and reported as one content-free stderr line per process.
/// Connections are unpooled and short-lived so concurrent CLI processes,
/// the MCP host, and a dashboard reader can share the file.
/// </summary>
public sealed class ActivityJournal : IActivityJournal
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string _path;
    private readonly bool _storeSensitive;
    private readonly TextWriter _log;
    private readonly TimeProvider _time;
    private int _warned;

    private ActivityJournal(string path, bool storeSensitive, TextWriter log, TimeProvider time)
    {
        _path = path;
        _storeSensitive = storeSensitive;
        _log = log;
        _time = time;
    }

    public static IActivityJournal Open(JournalConfig config, TextWriter log) =>
        Open(SqlHarnessPaths.ActivityDatabase, config, log, TimeProvider.System);

    internal static IActivityJournal Open(string path, JournalConfig config, TextWriter log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        if (!config.Enabled)
            return NullActivityJournal.Instance;

        var journal = new ActivityJournal(path, config.StoreSensitive, log, time);
        try
        {
            journal.Initialize();
            return journal;
        }
        catch (JournalSchemaTooNewException)
        {
            log.WriteLine("sqlharness: activity journal schema is newer than this sqlharness; journal disabled.");
            return NullActivityJournal.Instance;
        }
        catch (Exception)
        {
            log.WriteLine("sqlharness: activity journal unavailable; continuing without it.");
            return NullActivityJournal.Instance;
        }
    }

    public JournalHandle? Begin(SessionIdentity session, OperationStart start)
    {
        try
        {
            var now = Timestamp(_time.GetUtcNow());
            using var connection = Connect();
            using var transaction = connection.BeginTransaction();
            var sessionId = UpsertSession(connection, transaction, session, now);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO operations (session_id, operation, host_pid, host_started_at, started_at, updated_at,
                    status, profile, vars_json, mutation_requested, sql_hash, candidate_sql_hash, sql_text, candidate_sql_text)
                VALUES ($session, $operation, $hostPid, $hostStarted, $now, $now,
                    'running', $profile, $vars, $mutation, $sqlHash, $candidateHash, $sqlText, $candidateText)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$operation", start.Operation);
            insert.Parameters.AddWithValue("$hostPid", session.HostPid);
            insert.Parameters.AddWithValue("$hostStarted", Nullable(session.HostStartedAt));
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$profile", (object?)start.Profile ?? DBNull.Value);
            insert.Parameters.AddWithValue("$vars", (object?)VarsJson(start.Vars) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$mutation", start.MutationRequested ? 1 : 0);
            insert.Parameters.AddWithValue("$sqlHash", (object?)start.SqlHash ?? DBNull.Value);
            insert.Parameters.AddWithValue("$candidateHash", (object?)start.CandidateSqlHash ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sqlText", _storeSensitive ? (object?)start.SqlText ?? DBNull.Value : DBNull.Value);
            insert.Parameters.AddWithValue("$candidateText", _storeSensitive ? (object?)start.CandidateSqlText ?? DBNull.Value : DBNull.Value);
            var id = (long)insert.ExecuteScalar()!;
            transaction.Commit();
            return new JournalHandle(id);
        }
        catch (Exception)
        {
            Warn();
            return null;
        }
    }

    public void Complete(JournalHandle? handle, OperationEnd end)
    {
        if (handle is null)
            return;
        try
        {
            var now = Timestamp(_time.GetUtcNow());
            using var connection = Connect();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE operations SET status = $status, exit_code = $exit, error_kind = $error, duration_ms = $duration,
                    engine = $engine, server = $server, database = $database, result_sets = $sets, rows_returned = $rows,
                    finished_at = $now, updated_at = $now
                WHERE id = $id;
                UPDATE sessions SET last_seen = $now
                WHERE id = (SELECT session_id FROM operations WHERE id = $id);
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$status", end.Status);
            update.Parameters.AddWithValue("$exit", end.ExitCode);
            update.Parameters.AddWithValue("$error", (object?)end.ErrorKind ?? DBNull.Value);
            update.Parameters.AddWithValue("$duration", end.DurationMilliseconds);
            update.Parameters.AddWithValue("$engine", (object?)end.Engine ?? DBNull.Value);
            update.Parameters.AddWithValue("$server", (object?)end.Server ?? DBNull.Value);
            update.Parameters.AddWithValue("$database", (object?)end.Database ?? DBNull.Value);
            update.Parameters.AddWithValue("$sets", (object?)end.ResultSets ?? DBNull.Value);
            update.Parameters.AddWithValue("$rows", (object?)end.RowsReturned ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (Exception)
        {
            Warn();
        }
    }

    public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted)
    {
        if (handle is null)
            return;
        try
        {
            using var connection = Connect();
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE operations SET raw_tokens = $raw, emitted_tokens = $emitted, updated_at = $now WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$raw", raw is null ? DBNull.Value : raw.EstimatedTokenCount);
            update.Parameters.AddWithValue("$emitted", emitted.EstimatedTokenCount);
            update.Parameters.AddWithValue("$now", Timestamp(_time.GetUtcNow()));
            update.ExecuteNonQuery();
        }
        catch (Exception)
        {
            Warn();
        }
    }

    private void Initialize()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        OwnerOnlyFiles.Directory(directory);
        try
        {
            Migrate();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            Quarantine();
            Migrate();
        }
    }

    private void Migrate()
    {
        using var connection = Connect();
        var version = UserVersion(connection);
        if (version > JournalSchema.CurrentVersion)
            throw new JournalSchemaTooNewException();
        if (version == JournalSchema.CurrentVersion)
            return;

        // auto_vacuum only takes effect before the first table exists; WAL persists in the file.
        Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL;");
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            // Re-check under the write lock: a concurrent process may have migrated first.
            var locked = UserVersion(connection);
            if (locked > JournalSchema.CurrentVersion)
                throw new JournalSchemaTooNewException();
            if (locked < 1)
                Execute(connection, JournalSchema.Version1);
            Execute(connection, $"PRAGMA user_version = {JournalSchema.CurrentVersion};");
            Execute(connection, "COMMIT;");
        }
        catch
        {
            Execute(connection, "ROLLBACK;");
            throw;
        }

        ProtectFiles();
    }

    private void Quarantine()
    {
        var suffix = ".corrupt-" + _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        foreach (var side in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + side;
            if (File.Exists(file))
                File.Move(file, _path + suffix + side);
        }
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            // Microsoft.Data.Sqlite retries SQLITE_BUSY until the command timeout;
            // 1 s is its minimum and bounds how long a locked journal can delay an agent.
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON;");
        ProtectFiles();
        return connection;
    }

    private void ProtectFiles()
    {
        OwnerOnlyFiles.File(_path);
        OwnerOnlyFiles.File(_path + "-wal");
        OwnerOnlyFiles.File(_path + "-shm");
    }

    private static long UpsertSession(SqliteConnection connection, SqliteTransaction transaction, SessionIdentity session, string now)
    {
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO sessions (session_key, agent_kind, transport, source, client_name, client_version, mcp_mode,
                agent_pid, agent_started_at, host_pid, host_started_at, cwd, first_seen, last_seen)
            VALUES ($key, $kind, $transport, $source, $clientName, $clientVersion, $mcpMode,
                $agentPid, $agentStarted, $hostPid, $hostStarted, $cwd, $now, $now)
            ON CONFLICT(session_key) DO UPDATE SET
                last_seen = excluded.last_seen,
                client_name = COALESCE(sessions.client_name, excluded.client_name),
                client_version = COALESCE(sessions.client_version, excluded.client_version)
            RETURNING id;
            """;
        upsert.Parameters.AddWithValue("$key", session.SessionKey);
        upsert.Parameters.AddWithValue("$kind", session.AgentKind);
        upsert.Parameters.AddWithValue("$transport", session.Transport == JournalTransport.Mcp ? "mcp" : "cli");
        upsert.Parameters.AddWithValue("$source", session.Source);
        upsert.Parameters.AddWithValue("$clientName", (object?)session.ClientName ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$clientVersion", (object?)session.ClientVersion ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$mcpMode", (object?)session.McpMode ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$agentPid", (object?)session.AgentPid ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$agentStarted", Nullable(session.AgentStartedAt));
        upsert.Parameters.AddWithValue("$hostPid", session.HostPid);
        upsert.Parameters.AddWithValue("$hostStarted", Nullable(session.HostStartedAt));
        upsert.Parameters.AddWithValue("$cwd", (object?)session.Cwd ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$now", now);
        return (long)upsert.ExecuteScalar()!;
    }

    private static string? VarsJson(IReadOnlyDictionary<string, string>? vars) =>
        vars is null || vars.Count == 0
            ? null
            : JsonSerializer.Serialize(new SortedDictionary<string, string>(
                vars.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));

    private static object Nullable(DateTimeOffset? value) => value is null ? DBNull.Value : Timestamp(value.Value);

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static long UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Warn()
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            _log.WriteLine("sqlharness: activity journal write failed; continuing without recording.");
    }

    private sealed class JournalSchemaTooNewException : Exception;
}