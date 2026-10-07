using System.Globalization;
using System.IO.Compression;
using System.Text;
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

    private static readonly JsonSerializerOptions WaitJson = new(JsonSerializerDefaults.Web);

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
            SafeWriteLine(log, "sqlharness: activity journal schema is newer than this sqlharness; journal disabled.");
            return NullActivityJournal.Instance;
        }
        catch (Exception)
        {
            SafeWriteLine(log, "sqlharness: activity journal unavailable; continuing without it.");
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

    public bool Complete(JournalHandle? handle, OperationEnd end)
    {
        if (handle is null)
            return false;
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
                    raw_tokens = COALESCE($raw, raw_tokens), artifact_dir = $artifact, summary_json = $summary, finished_at = $now, updated_at = $now
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
            update.Parameters.AddWithValue("$raw", (object?)end.RawTokens ?? DBNull.Value);
            update.Parameters.AddWithValue("$artifact", (object?)end.ArtifactDirectory ?? DBNull.Value);
            update.Parameters.AddWithValue("$summary", (object?)end.SummaryJson ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
        catch (Exception)
        {
            Warn();
            return false;
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

    public bool RecordWatchProgress(JournalHandle? handle, WatchProgress progress)
    {
        if (handle is null || progress is null)
            return false;
        try
        {
            using var connection = Connect();
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE operations SET progress_json = $progress, updated_at = $now
                WHERE id = $id AND status = 'running';
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$progress", string.Create(CultureInfo.InvariantCulture,
                $$"""{"polls":{{progress.Polls}},"changedPolls":{{progress.ChangedPolls}},"elapsedMs":{{progress.ElapsedMilliseconds}}}"""));
            update.Parameters.AddWithValue("$now", Timestamp(_time.GetUtcNow()));
            update.ExecuteNonQuery();
            return true;
        }
        catch (Exception)
        {
            Warn();
            return false;
        }
    }

    public void RecordBenchmark(JournalHandle? handle, BenchmarkJournalRecord record)
    {
        if (handle is null || record is null || record.Variants.Count == 0)
            return;
        try
        {
            var now = Timestamp(_time.GetUtcNow());
            using var connection = Connect();
            using var transaction = connection.BeginTransaction();
            for (var ordinal = 0; ordinal < record.Variants.Count; ordinal++)
            {
                var variant = record.Variants[ordinal];
                var metricId = InsertMetric(connection, transaction, handle.OperationId, ordinal, variant);
                foreach (var table in variant.TableIo)
                    InsertTableIo(connection, transaction, metricId, table);
                foreach (var link in variant.PlanLinks)
                {
                    using var insert = Command(connection, transaction, """
                        INSERT OR IGNORE INTO operation_plans (metric_id, repetition, ordinal, plan_hash)
                        VALUES ($metric, $repetition, $ordinal, $hash);
                        """);
                    insert.Parameters.AddWithValue("$metric", metricId);
                    insert.Parameters.AddWithValue("$repetition", link.Repetition);
                    insert.Parameters.AddWithValue("$ordinal", link.Ordinal);
                    insert.Parameters.AddWithValue("$hash", link.Hash);
                    insert.ExecuteNonQuery();
                }
            }

            if (_storeSensitive)
            {
                foreach (var document in record.PlanDocuments)
                {
                    var bytes = Encoding.UTF8.GetBytes(document.Document);
                    using var insert = Command(connection, transaction, """
                        INSERT OR IGNORE INTO plans (hash, format, raw_size, gz, first_seen)
                        VALUES ($hash, $format, $size, $gz, $now);
                        """);
                    insert.Parameters.AddWithValue("$hash", document.Hash);
                    insert.Parameters.AddWithValue("$format", document.Format);
                    insert.Parameters.AddWithValue("$size", bytes.LongLength);
                    insert.Parameters.AddWithValue("$gz", Gzip(bytes));
                    insert.Parameters.AddWithValue("$now", now);
                    insert.ExecuteNonQuery();
                }
            }

            using var touch = Command(connection, transaction, "UPDATE operations SET updated_at = $now WHERE id = $id;");
            touch.Parameters.AddWithValue("$now", now);
            touch.Parameters.AddWithValue("$id", handle.OperationId);
            touch.ExecuteNonQuery();
            transaction.Commit();
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
            if (locked < 2)
                Execute(connection, JournalSchema.Version2);
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

    private static long InsertMetric(SqliteConnection connection, SqliteTransaction transaction, long operationId, int ordinal, JournalVariantMetrics v)
    {
        using var insert = Command(connection, transaction, """
            INSERT INTO operation_metrics (operation_id, ordinal, variant, parameter_set, matrix_cell, runs,
                elapsed_ms_min, elapsed_ms_median, elapsed_ms_max, cpu_ms_min, cpu_ms_median, cpu_ms_max,
                logical_reads_min, logical_reads_median, logical_reads_max,
                grant_requested_kb, grant_granted_kb, grant_max_used_kb, dop, compile_time_ms, compile_cpu_ms,
                spill_count, has_warnings, has_implicit_conversion, missing_index_count, waits_json,
                pg_shared_hit, pg_shared_read, pg_shared_dirtied, pg_shared_written, pg_temp_read, pg_temp_written)
            VALUES ($operation, $ordinal, $variant, $set, $cell, $runs,
                $eMin, $eMed, $eMax, $cMin, $cMed, $cMax, $rMin, $rMed, $rMax,
                $gReq, $gGrant, $gUsed, $dop, $compileTime, $compileCpu,
                $spills, $warnings, $implicit, $missing, $waits,
                $pgHit, $pgRead, $pgDirtied, $pgWritten, $pgTempRead, $pgTempWritten)
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("$operation", operationId);
        insert.Parameters.AddWithValue("$ordinal", ordinal);
        insert.Parameters.AddWithValue("$variant", v.Variant);
        insert.Parameters.AddWithValue("$set", (object?)v.ParameterSet ?? DBNull.Value);
        insert.Parameters.AddWithValue("$cell", (object?)v.MatrixCell ?? DBNull.Value);
        insert.Parameters.AddWithValue("$runs", v.Runs);
        AddSpread(insert, "$e", v.ElapsedMilliseconds);
        AddSpread(insert, "$c", v.CpuMilliseconds);
        AddSpread(insert, "$r", v.LogicalReads);
        insert.Parameters.AddWithValue("$gReq", (object?)v.GrantRequestedKb ?? DBNull.Value);
        insert.Parameters.AddWithValue("$gGrant", (object?)v.GrantGrantedKb ?? DBNull.Value);
        insert.Parameters.AddWithValue("$gUsed", (object?)v.GrantMaxUsedKb ?? DBNull.Value);
        insert.Parameters.AddWithValue("$dop", (object?)v.Dop ?? DBNull.Value);
        insert.Parameters.AddWithValue("$compileTime", (object?)v.CompileTimeMs ?? DBNull.Value);
        insert.Parameters.AddWithValue("$compileCpu", (object?)v.CompileCpuMs ?? DBNull.Value);
        insert.Parameters.AddWithValue("$spills", v.SpillCount);
        insert.Parameters.AddWithValue("$warnings", v.HasWarnings ? 1 : 0);
        insert.Parameters.AddWithValue("$implicit", v.HasImplicitConversion ? 1 : 0);
        insert.Parameters.AddWithValue("$missing", v.MissingIndexCount);
        insert.Parameters.AddWithValue("$waits", v.Waits.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(v.Waits, WaitJson));
        insert.Parameters.AddWithValue("$pgHit", (object?)v.Postgres?.SharedHit ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgRead", (object?)v.Postgres?.SharedRead ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgDirtied", (object?)v.Postgres?.SharedDirtied ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgWritten", (object?)v.Postgres?.SharedWritten ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgTempRead", (object?)v.Postgres?.TempRead ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pgTempWritten", (object?)v.Postgres?.TempWritten ?? DBNull.Value);
        return (long)insert.ExecuteScalar()!;
    }

    private static void InsertTableIo(SqliteConnection connection, SqliteTransaction transaction, long metricId, JournalTableIo t)
    {
        using var insert = Command(connection, transaction, """
            INSERT OR REPLACE INTO operation_table_io (metric_id, table_name, logical_reads, scan_count, physical_reads,
                page_server_reads, read_ahead_reads, lob_logical_reads, lob_physical_reads, lob_read_ahead_reads, cold_runs)
            VALUES ($metric, $table, $logical, $scan, $physical, $pageServer, $readAhead, $lobLogical, $lobPhysical, $lobReadAhead, $cold);
            """);
        insert.Parameters.AddWithValue("$metric", metricId);
        insert.Parameters.AddWithValue("$table", t.Table);
        insert.Parameters.AddWithValue("$logical", t.LogicalReads);
        insert.Parameters.AddWithValue("$scan", (object?)t.ScanCount ?? DBNull.Value);
        insert.Parameters.AddWithValue("$physical", (object?)t.PhysicalReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pageServer", (object?)t.PageServerReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$readAhead", (object?)t.ReadAheadReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$lobLogical", (object?)t.LobLogicalReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$lobPhysical", (object?)t.LobPhysicalReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$lobReadAhead", (object?)t.LobReadAheadReads ?? DBNull.Value);
        insert.Parameters.AddWithValue("$cold", t.ColdRuns);
        insert.ExecuteNonQuery();
    }

    private static void AddSpread(SqliteCommand command, string prefix, CompareDistribution? value)
    {
        command.Parameters.AddWithValue(prefix + "Min", (object?)value?.Min ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "Med", (object?)value?.Median ?? DBNull.Value);
        command.Parameters.AddWithValue(prefix + "Max", (object?)value?.Max ?? DBNull.Value);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes);
        return output.ToArray();
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
            SafeWriteLine(_log, "sqlharness: activity journal write failed; continuing without recording.");
    }

    /// <summary>Diagnostics are best-effort too: a closed or broken stderr must not escape a catch block.</summary>
    private static void SafeWriteLine(TextWriter log, string line)
    {
        try
        {
            log.WriteLine(line);
        }
        catch (Exception)
        {
        }
    }

    private sealed class JournalSchemaTooNewException : Exception;
}