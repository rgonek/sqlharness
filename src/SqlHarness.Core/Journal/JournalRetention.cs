using System.Globalization;

using Microsoft.Data.Sqlite;

namespace SqlHarness.Core;

public sealed record RetentionResult(bool Ran, int DeletedOperations, int DeletedPlans, int DeletedSessions)
{
    internal static RetentionResult Skipped { get; } = new(false, 0, 0, 0);
}

/// <summary>
/// Opt-in journal retention: operations older than maxAgeDays, then unreferenced plans and
/// empty sessions, then the oldest operations while the used size exceeds maxSizeMb, and
/// finally an incremental vacuum. A running operation is deleted only when its host process
/// is gone (abandoned). Work is done in short BEGIN IMMEDIATE batches so concurrent journal
/// writers (1 s budget) are never starved. Never throws.
/// </summary>
public static class JournalRetention
{
    public const int BatchSize = 500;

    /// <summary>Operations removed per size-cap step before the used size is measured again.</summary>
    public const int SizeStep = 100;

    /// <summary>Free pages returned to the file system per incremental-vacuum write transaction.</summary>
    internal const int VacuumStep = 1024;

    private const long AutoVacuumIncremental = 2;

    public static RetentionResult Run(string databasePath, JournalConfig journal, IProcessInfo processes, TimeProvider time) =>
        Run(databasePath, journal, processes, time, onBatchCommitted: null);

    internal static RetentionResult Run(string databasePath, JournalConfig journal, IProcessInfo processes, TimeProvider time, Action? onBatchCommitted)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!journal.Enabled || !journal.Retention.Enabled || !File.Exists(databasePath))
            return RetentionResult.Skipped;
        try
        {
            using var connection = Connect(databasePath);
            if (Scalar(connection, "PRAGMA user_version;") != JournalSchema.CurrentVersion)
                return RetentionResult.Skipped;

            // A lookup that fails proves nothing: retention keeps that running row.
            var liveness = new ProcessLiveness(processes, new DeadProcesses(), failedLookupIsAlive: true);
            var cutoff = time.GetUtcNow().AddDays(-journal.Retention.MaxAgeDays).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

            var operations = DeleteOperations(connection, liveness, "AND started_at < $cutoff", cutoff, int.MaxValue, onBatchCommitted);
            var (plans, sessions) = DeleteOrphans(connection, cutoff);

            var limit = (long)journal.Retention.MaxSizeMb * 1024 * 1024;
            while (UsedBytes(connection) > limit)
            {
                var deleted = DeleteOperations(connection, liveness, string.Empty, cutoff, SizeStep, onBatchCommitted);
                var (morePlans, moreSessions) = DeleteOrphans(connection, cutoff: null);
                operations += deleted;
                plans += morePlans;
                sessions += moreSessions;
                if (deleted == 0)
                    break;
            }

            IncrementalVacuum(connection);
            Execute(connection, "PRAGMA wal_checkpoint(PASSIVE);");
            return new RetentionResult(true, operations, plans, sessions);
        }
        catch (Exception)
        {
            return RetentionResult.Skipped;
        }
    }

    /// <summary>
    /// Deletes oldest-first in batches; returns the number of operations deleted (at most <paramref name="max"/>).
    /// Live running rows are skipped by id (keyset), so they never block older deletable rows behind them.
    /// </summary>
    private static int DeleteOperations(SqliteConnection connection, ProcessLiveness liveness, string extraFilter, string cutoff, int max, Action? onBatchCommitted)
    {
        var deleted = 0;
        var after = 0L;
        while (deleted < max)
        {
            var candidates = new List<(long Id, string Status, long Pid, string? Started)>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = $"""
                    SELECT id, status, host_pid, host_started_at FROM operations
                    WHERE id > $after {extraFilter} ORDER BY id LIMIT $limit;
                    """;
                select.Parameters.AddWithValue("$after", after);
                select.Parameters.AddWithValue("$cutoff", cutoff);
                select.Parameters.AddWithValue("$limit", BatchSize);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    candidates.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            if (candidates.Count == 0)
                return deleted;

            var ids = candidates
                .Where(row => row.Status != "running" || !liveness.IsRunningAlive(row.Pid, row.Started))
                .Select(row => row.Id)
                .Take(max - deleted)
                .ToArray();
            after = ids.Length == max - deleted ? ids[^1] : candidates[^1].Id;

            if (ids.Length > 0)
            {
                Execute(connection, "BEGIN IMMEDIATE;");
                try
                {
                    // A finished row never returns to running, and an abandoned row's host is gone,
                    // so the liveness decision made before the write lock still holds.
                    Execute(connection, $"DELETE FROM operations WHERE id IN ({string.Join(",", ids)});");
                    Execute(connection, "COMMIT;");
                }
                catch
                {
                    Execute(connection, "ROLLBACK;");
                    throw;
                }

                deleted += ids.Length;
                onBatchCommitted?.Invoke();
            }

            if (candidates.Count < BatchSize)
                return deleted;
        }

        return deleted;
    }

    private static (int Plans, int Sessions) DeleteOrphans(SqliteConnection connection, string? cutoff)
    {
        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            int plans;
            using (var deletePlans = connection.CreateCommand())
            {
                deletePlans.CommandText = "DELETE FROM plans WHERE hash NOT IN (SELECT plan_hash FROM operation_plans);";
                plans = deletePlans.ExecuteNonQuery();
            }

            int sessions;
            using (var deleteSessions = connection.CreateCommand())
            {
                deleteSessions.CommandText = """
                    DELETE FROM sessions
                    WHERE NOT EXISTS (SELECT 1 FROM operations o WHERE o.session_id = sessions.id)
                      AND ($cutoff IS NULL OR last_seen < $cutoff);
                    """;
                deleteSessions.Parameters.AddWithValue("$cutoff", (object?)cutoff ?? DBNull.Value);
                sessions = deleteSessions.ExecuteNonQuery();
            }

            Execute(connection, "COMMIT;");
            return (plans, sessions);
        }
        catch
        {
            Execute(connection, "ROLLBACK;");
            throw;
        }
    }

    /// <summary>
    /// Returns free pages to the file system in short steps, each its own write transaction.
    /// A database whose auto_vacuum is not INCREMENTAL (every journal is created with it)
    /// keeps its free pages for reuse; retention does not run a full VACUUM to convert it.
    /// </summary>
    private static void IncrementalVacuum(SqliteConnection connection)
    {
        if (Scalar(connection, "PRAGMA auto_vacuum;") != AutoVacuumIncremental)
            return;
        var free = Scalar(connection, "PRAGMA freelist_count;");
        while (free > 0)
        {
            Execute(connection, $"PRAGMA incremental_vacuum({VacuumStep});");
            var remaining = Scalar(connection, "PRAGMA freelist_count;");
            if (remaining >= free)
                return;
            free = remaining;
        }
    }

    private static long UsedBytes(SqliteConnection connection) =>
        (Scalar(connection, "PRAGMA page_count;") - Scalar(connection, "PRAGMA freelist_count;")) * Scalar(connection, "PRAGMA page_size;");

    private static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 250; PRAGMA foreign_keys = ON;");
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}