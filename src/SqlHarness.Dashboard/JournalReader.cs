using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

/// <summary>
/// Read-only queries over activity.db. Every call opens its own short-lived,
/// unpooled read-only connection; a missing database reads as empty. Running
/// rows are reported as <c>abandoned</c> when their host process is gone.
/// </summary>
public sealed class JournalReader(string databasePath, IProcessInfo processes)
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 200;
    public const int SessionOperationLimit = 500;
    public const int TopLimit = 20;
    internal const long OverGrantMinimumKb = 1024;

    // Shared across reads and threads (live feed ticks, API requests): dead hosts stay dead.
    private readonly DeadProcesses _dead = new();

    private static readonly string OperationColumns = $"""
        o.id, o.session_id, s.agent_kind, o.operation, o.status, o.exit_code, o.error_kind, o.started_at, o.updated_at,
        o.finished_at, o.duration_ms, o.profile, o.engine, o.server, o.database, o.mutation_requested, o.sql_hash,
        o.rows_returned, o.progress_json, o.host_pid, o.host_started_at,
        (SELECT m.logical_reads_median FROM operation_metrics m WHERE m.operation_id = o.id ORDER BY m.ordinal LIMIT 1) AS reads_median,
        EXISTS (SELECT 1 FROM operation_metrics m WHERE m.operation_id = o.id AND m.spill_count > 0) AS has_spill,
        EXISTS (SELECT 1 FROM operation_metrics m JOIN operation_table_io t ON t.metric_id = m.id
                WHERE m.operation_id = o.id AND t.cold_runs > 0) AS cold_cache,
        EXISTS (SELECT 1 FROM operation_metrics m WHERE m.operation_id = o.id AND m.grant_granted_kb >= {OverGrantMinimumKb}
                AND m.grant_max_used_kb IS NOT NULL AND m.grant_max_used_kb * 4 < m.grant_granted_kb) AS over_granted,
        o.error_message
        """;

    private const string SessionColumns = """
        s.id, s.session_key, s.agent_kind, s.transport, s.source, s.client_name, s.client_version, s.mcp_mode, s.cwd,
        s.first_seen, s.last_seen,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id) AS ops,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id AND o.status = 'failed') AS failed,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id AND o.status = 'rejected') AS rejected
        """;

    public long? SchemaVersion()
    {
        using var connection = OpenReadOnly();
        return connection is null ? null : Scalar<long>(connection, "PRAGMA user_version;", []);
    }

    /// <summary>Read-only, unpooled connection; null when the database does not exist yet.</summary>
    public SqliteConnection? OpenReadOnly()
    {
        if (!File.Exists(databasePath))
            return null;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Read-only connection inside one deferred read transaction, so a method that
    /// runs several queries sees a single WAL snapshot. Disposing the connection
    /// ends the transaction; nothing is ever written.
    /// </summary>
    private SqliteConnection? OpenSnapshot()
    {
        var connection = OpenReadOnly();
        if (connection is null)
            return null;
        try
        {
            using var begin = connection.CreateCommand();
            begin.CommandText = "BEGIN DEFERRED;";
            begin.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Page<SessionSummary> Sessions(SessionQuery query)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new Page<SessionSummary>([], null);
        var limit = Clamp(query.Limit);
        (string, object?)[] parameters =
        [
            ("$agent", query.Agent),
            ("$transport", query.Transport),
            ("$from", Iso(query.From)),
            ("$to", Iso(query.To)),
            ("$cursor", query.Cursor),
            ("$limit", limit + 1),
        ];
        var rows = Query(connection, $"""
            SELECT {SessionColumns} FROM sessions s
            WHERE ($agent IS NULL OR s.agent_kind = $agent)
              AND ($transport IS NULL OR s.transport = $transport)
              AND ($from IS NULL OR s.last_seen >= $from)
              AND ($to IS NULL OR s.first_seen < $to)
              AND ($cursor IS NULL OR s.id < $cursor)
            ORDER BY s.id DESC LIMIT $limit;
            """,
            parameters,
            ReadSessionRow);
        var page = rows.Take(limit).ToArray();
        return new Page<SessionSummary>(WithRunning(connection, page), rows.Count > limit ? page[^1].Id : null);
    }

    public SessionDetail? Session(long id)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        var session = Query(connection, $"SELECT {SessionColumns} FROM sessions s WHERE s.id = $id;", [("$id", id)], ReadSessionRow)
            .FirstOrDefault();
        if (session is null)
            return null;
        var liveness = new ProcessLiveness(processes, _dead);
        var operations = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE o.session_id = $id ORDER BY o.id DESC LIMIT $limit;
            """, [("$id", id), ("$limit", SessionOperationLimit)], reader => ReadOperation(reader, liveness));
        return new SessionDetail(WithRunning(connection, [session])[0], operations);
    }

    public Page<OperationSummary> Operations(OperationQuery query)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new Page<OperationSummary>([], null);
        var limit = Clamp(query.Limit);
        // abandoned is computed, not stored: filter running rows in SQL, then by liveness here.
        // A live-filtered page keeps scanning older batches until it has limit + 1 matches
        // or the rows run out, so dead rows never yield an empty page with a cursor.
        var liveFiltered = query.Status is "running" or "abandoned";
        var variableFiltered = query.Dimensions is { Count: > 0 };
        var storedStatus = liveFiltered ? "running" : query.Status;
        var liveness = new ProcessLiveness(processes, _dead);
        var matches = new List<OperationSummary>();
        var cursor = query.Cursor;
        while (true)
        {
            (string, object?)[] parameters =
            [
                ("$session", query.SessionId),
                ("$status", storedStatus),
                ("$operation", query.Operation),
                ("$profile", query.Profile),
                ("$from", Iso(query.From)),
                ("$to", Iso(query.To)),
                ("$cursor", cursor),
                ("$limit", limit + 1),
            ];
            var rows = Query(connection, $"""
                SELECT {OperationColumns}, o.vars_json FROM operations o JOIN sessions s ON s.id = o.session_id
                WHERE ($session IS NULL OR o.session_id = $session)
                  AND ($status IS NULL OR o.status = $status)
                  AND ($operation IS NULL OR o.operation = $operation)
                  AND ($profile IS NULL OR o.profile = $profile)
                  AND ($from IS NULL OR o.started_at >= $from)
                  AND ($to IS NULL OR o.started_at < $to)
                  AND ($cursor IS NULL OR o.id < $cursor)
                ORDER BY o.id DESC LIMIT $limit;
                """,
                parameters,
                reader => (Operation: ReadOperation(reader, liveness),
                    Variables: OperationDimensionResolver.ReadRecordedValues(NullableString(reader, 26))));
            var filteredRows = rows.Where(row => !variableFiltered
                || OperationDimensionResolver.Matches(query.Dimensions, row.Variables));
            matches.AddRange(liveFiltered
                ? filteredRows.Where(row => row.Operation.Status == query.Status).Select(row => row.Operation)
                : filteredRows.Select(row => row.Operation));
            if (matches.Count > limit || rows.Count <= limit)
                break;
            cursor = rows[^1].Operation.Id;
        }

        var page = matches.Take(limit).ToArray();
        var next = matches.Count > limit ? page[^1].Id : (long?)null;
        return new Page<OperationSummary>(page, next);
    }

    public OperationDetail? Operation(
        long id,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? profileDimensions = null)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        var liveness = new ProcessLiveness(processes, _dead);
        var summary = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id WHERE o.id = $id;
            """, [("$id", id)], reader => ReadOperation(reader, liveness)).FirstOrDefault();
        if (summary is null)
            return null;

        var extra = Query(connection, """
            SELECT vars_json, candidate_sql_hash, sql_text, candidate_sql_text, raw_tokens, emitted_tokens, artifact_dir, summary_json
            FROM operations WHERE id = $id;
            """, [("$id", id)], reader => (
                VarsJson: NullableString(reader, 0),
                CandidateHash: NullableString(reader, 1),
                SqlText: NullableString(reader, 2),
                CandidateText: NullableString(reader, 3),
                Raw: NullableLong(reader, 4),
                Emitted: NullableLong(reader, 5),
                Artifact: NullableString(reader, 6),
                Summary: Json(NullableString(reader, 7)))).Single();
        var session = Query(connection, $"SELECT {SessionColumns} FROM sessions s WHERE s.id = $id;",
            [("$id", summary.SessionId)], ReadSessionRow).Single();

        var recordedDimensions = OperationDimensionResolver.ReadRecordedValues(extra.VarsJson);
        var dimensionNames = (summary.Profile is not null && profileDimensions is not null
                              && profileDimensions.TryGetValue(summary.Profile, out var configuredDimensions)
            ? configuredDimensions
            : Array.Empty<string>())
            .Concat(OperationDimensionResolver.ReadDimensionNames(extra.VarsJson))
            .Distinct(StringComparer.Ordinal);
        var dimensions = new OperationDimensions(OperationDimensionResolver.Resolve(
            dimensionNames,
            recordedDimensions));

        return new OperationDetail(
            summary,
            WithRunning(connection, [session])[0],
            Vars(extra.VarsJson),
            extra.CandidateHash,
            extra.SqlText,
            extra.CandidateText,
            extra.Raw,
            extra.Emitted,
            extra.Artifact,
            extra.Summary,
            Variants(connection, id),
            dimensions);
    }

    public StoredPlan? Plan(string hash)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return null;
        return Query(connection, "SELECT hash, format, gz FROM plans WHERE hash = $hash;", [("$hash", hash)], reader =>
        {
            using var gzip = new GZipStream(new MemoryStream((byte[])reader.GetValue(2)), CompressionMode.Decompress);
            using var text = new StreamReader(gzip, Encoding.UTF8);
            return new StoredPlan(reader.GetString(0), reader.GetString(1), text.ReadToEnd());
        }).FirstOrDefault();
    }

    public DashboardStats Stats(
        StatsQuery query,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? profileDimensions = null)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new DashboardStats([], [], [], [], [], [], [], [], [], new TokenStat(0, 0), 0, 0, [],
                new ProfileDimensionStats(query.Profile,
                    query.Profile is not null && profileDimensions?.ContainsKey(query.Profile) == true, 0, [], []));
        (string, object?)[] window = [("$from", Iso(query.From)), ("$to", Iso(query.To))];
        const string inWindow = "($from IS NULL OR o.started_at >= $from) AND ($to IS NULL OR o.started_at < $to)";

        var perDay = Query(connection, $"""
            SELECT substr(o.started_at, 1, 10) AS day, s.agent_kind, COUNT(*) FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE {inWindow} GROUP BY day, s.agent_kind ORDER BY day, s.agent_kind;
            """, window, r => new DayAgentCount(r.GetString(0), r.GetString(1), r.GetInt32(2)));

        var liveness = new ProcessLiveness(processes, _dead);
        var statuses = Query(connection, $"""
            SELECT o.status, COUNT(*) FROM operations o WHERE {inWindow} AND o.status <> 'running' GROUP BY o.status;
            """, window, r => new KeyCount(r.GetString(0), r.GetInt32(1))).ToList();
        var running = Query(connection, $"""
            SELECT o.host_pid, o.host_started_at FROM operations o WHERE {inWindow} AND o.status = 'running';
            """, window, r => liveness.Status("running", r.GetInt64(0), NullableString(r, 1)));
        foreach (var group in running.GroupBy(status => status))
            statuses.Add(new KeyCount(group.Key, group.Count()));

        var exitCodes = Query(connection, $"""
            SELECT CAST(o.exit_code AS TEXT), COUNT(*) FROM operations o WHERE {inWindow} AND o.exit_code IS NOT NULL
            GROUP BY o.exit_code ORDER BY o.exit_code;
            """, window, r => new KeyCount(r.GetString(0), r.GetInt32(1)));
        var operations = Query(connection, $"""
            SELECT o.operation, COUNT(*) FROM operations o WHERE {inWindow} GROUP BY o.operation ORDER BY COUNT(*) DESC, o.operation;
            """, window, r => new KeyCount(r.GetString(0), r.GetInt32(1)));

        const string sqlStats = "SELECT o.sql_hash, COUNT(*) AS n, COALESCE(SUM(o.duration_ms), 0) AS total, COALESCE(MAX(o.duration_ms), 0) FROM operations o";
        var byCount = Query(connection, $"""
            {sqlStats} WHERE {inWindow} AND o.sql_hash IS NOT NULL GROUP BY o.sql_hash ORDER BY n DESC, total DESC, o.sql_hash LIMIT {TopLimit};
            """, window, ReadSqlStat);
        var byDuration = Query(connection, $"""
            {sqlStats} WHERE {inWindow} AND o.sql_hash IS NOT NULL GROUP BY o.sql_hash ORDER BY total DESC, n DESC, o.sql_hash LIMIT {TopLimit};
            """, window, ReadSqlStat);

        var tables = Query(connection, $"""
            SELECT t.table_name, SUM(t.logical_reads * m.runs), COUNT(DISTINCT o.id)
            FROM operation_table_io t JOIN operation_metrics m ON m.id = t.metric_id JOIN operations o ON o.id = m.operation_id
            WHERE {inWindow} GROUP BY t.table_name ORDER BY 2 DESC, t.table_name LIMIT {TopLimit};
            """, window, r => new TableReadStat(r.GetString(0), r.GetInt64(1), r.GetInt32(2)));

        var waits = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (json, runs) in Query(connection, $"""
            SELECT m.waits_json, m.runs FROM operation_metrics m JOIN operations o ON o.id = m.operation_id
            WHERE {inWindow} AND m.waits_json IS NOT NULL;
            """, window, r => (r.GetString(0), r.GetInt32(1))))
        {
            if (Json(json) is not { ValueKind: JsonValueKind.Array } array)
                continue;
            foreach (var wait in array.EnumerateArray())
            {
                if (wait.ValueKind != JsonValueKind.Object
                    || !wait.TryGetProperty("averageWaitMs", out var average)
                    || average.ValueKind != JsonValueKind.Number
                    || !average.TryGetDouble(out var averageMs))
                    continue;
                var type = wait.TryGetProperty("waitType", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()!
                    : "UNKNOWN";
                waits[type] = waits.GetValueOrDefault(type) + averageMs * runs;
            }
        }

        var targets = Query(connection, $"""
            SELECT o.profile, o.database, COUNT(*), o.engine, o.server FROM operations o WHERE {inWindow}
            GROUP BY o.profile, o.database, o.engine, o.server ORDER BY COUNT(*) DESC, o.profile, o.engine, o.server, o.database LIMIT {TopLimit};
            """, window, r => new TargetStat(NullableString(r, 0), NullableString(r, 1), r.GetInt32(2), NullableString(r, 3), NullableString(r, 4)));

        // These aggregates intentionally read every operation in the requested window.
        // The bounded Targets list above is presentation data and cannot back dimension totals.
        var profileOperations = Query(connection, $"""
            SELECT o.profile, COUNT(*) FROM operations o WHERE {inWindow}
            GROUP BY o.profile ORDER BY COUNT(*) DESC, o.profile;
            """, window, r => new ProfileOperationCount(NullableString(r, 0), r.GetInt32(1)));
        (string, object?)[] dimensionWindow = [.. window, ("$profile", query.Profile)];
        var dimensionRows = Query(connection, $"""
            SELECT o.profile, o.engine, o.server, o.database, o.status, o.duration_ms, o.vars_json
            FROM operations o WHERE {inWindow} AND ($profile IS NOT NULL AND o.profile = $profile);
            """, dimensionWindow, r => new DimensionOperation(
                NullableString(r, 0), NullableString(r, 1), NullableString(r, 2), NullableString(r, 3),
                r.GetString(4), NullableLong(r, 5), NullableString(r, 6),
                OperationDimensionResolver.ReadRecordedValues(NullableString(r, 6))));
        var dimensionStats = AggregateProfileDimensions(query, dimensionRows, profileDimensions);
        var tokens = Query(connection, $"""
            SELECT COALESCE(SUM(o.raw_tokens), 0), COALESCE(SUM(o.emitted_tokens), 0) FROM operations o
            WHERE {inWindow} AND o.raw_tokens IS NOT NULL AND o.emitted_tokens IS NOT NULL;
            """, window, r => new TokenStat(r.GetInt64(0), r.GetInt64(1))).Single();
        var spills = Scalar<long>(connection, $"""
            SELECT COUNT(DISTINCT o.id) FROM operations o JOIN operation_metrics m ON m.operation_id = o.id
            WHERE {inWindow} AND m.spill_count > 0;
            """, window);
        var cold = Scalar<long>(connection, $"""
            SELECT COUNT(DISTINCT o.id) FROM operations o JOIN operation_metrics m ON m.operation_id = o.id
            JOIN operation_table_io t ON t.metric_id = m.id WHERE {inWindow} AND t.cold_runs > 0;
            """, window);

        return new DashboardStats(
            perDay,
            statuses.OrderBy(s => s.Key, StringComparer.Ordinal).ToArray(),
            exitCodes,
            operations,
            byCount,
            byDuration,
            tables,
            waits.OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal).Take(TopLimit)
                .Select(w => new WaitStat(w.Key, w.Value)).ToArray(),
            targets,
            tokens,
            (int)spills,
            (int)cold,
            profileOperations,
            dimensionStats);
    }

    private static ProfileDimensionStats AggregateProfileDimensions(
        StatsQuery query,
        IReadOnlyList<DimensionOperation> operations,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? profileDimensions)
    {
        var selectedProfileOperations = query.Profile is null
            ? Array.Empty<DimensionOperation>()
            : operations.Where(operation => string.Equals(operation.Profile, query.Profile, StringComparison.Ordinal)).ToArray();
        var knownNames = query.Profile is not null && profileDimensions is not null
                         && profileDimensions.TryGetValue(query.Profile, out var configured)
            ? configured
            : Array.Empty<string>();
        var profileDefinitionAvailable = query.Profile is not null && profileDimensions?.ContainsKey(query.Profile) == true;
        var dimensions = selectedProfileOperations.SelectMany(operation => operation.VariableNames)
            .Concat(knownNames).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var filtered = selectedProfileOperations.Where(operation =>
            OperationDimensionResolver.Matches(query.Dimensions, operation.Variables)).ToArray();
        var valueStats = new List<DimensionStat>(dimensions.Length);
        foreach (var dimension in dimensions)
        {
            var groups = filtered.GroupBy(operation => operation.Variables.TryGetValue(dimension, out var value)
                ? (IsUnknown: false, Value: value)
                : (IsUnknown: true, Value: OperationDimensionResolver.UnknownLabel));
            var values = groups.Select(group =>
            {
                var metrics = AggregateDimensionMetrics(group);
                return OperationDimensionResolver.ResolveValue(
                    dimension,
                    group.Key.IsUnknown ? null : group.Key.Value) with
                {
                    Operations = metrics.Operations,
                    Percentage = filtered.Length == 0 ? 0 : 100d * metrics.Operations / filtered.Length,
                    TotalDurationMs = metrics.TotalDurationMs,
                    DurationAvailableOperations = metrics.DurationAvailableOperations,
                    DurationUnavailableOperations = metrics.DurationUnavailableOperations,
                    Failed = metrics.Failed,
                    Rejected = metrics.Rejected,
                };
            }).OrderBy(value => value.IsUnknown ? 1 : 0)
              .ThenBy(value => value.Value, StringComparer.Ordinal)
              .ToArray();
            valueStats.Add(new DimensionStat(dimension, values));
        }

        var targets = filtered.GroupBy(operation =>
                (operation.Profile, operation.Database, operation.Engine, operation.Server))
            .Select(group => new TargetStat(group.Key.Profile, group.Key.Database, group.Count(), group.Key.Engine, group.Key.Server))
            .OrderByDescending(target => target.Count)
            .ThenBy(target => target.Profile, StringComparer.Ordinal)
            .ThenBy(target => target.Engine, StringComparer.Ordinal)
            .ThenBy(target => target.Server, StringComparer.Ordinal)
            .ThenBy(target => target.Database, StringComparer.Ordinal)
            .Take(TopLimit)
            .ToArray();
        var matrix = BuildDimensionMatrix(query, dimensions, filtered);
        return new ProfileDimensionStats(query.Profile, profileDefinitionAvailable, filtered.Length, valueStats, targets, matrix);
    }

    private static DimensionAggregate AggregateDimensionMetrics(IEnumerable<DimensionOperation> operations)
    {
        var rows = operations.ToArray();
        var durations = rows.Where(operation => operation.DurationMs.HasValue)
            .Select(operation => operation.DurationMs!.Value).ToArray();
        return new DimensionAggregate(rows.Length, durations.Length == 0 ? null : durations.Sum(), durations.Length,
            rows.Length - durations.Length, rows.Count(operation => operation.Status == "failed"),
            rows.Count(operation => operation.Status == "rejected"));
    }

    private static DimensionMatrixStats? BuildDimensionMatrix(
        StatsQuery query, IReadOnlyList<string> dimensions, IReadOnlyList<DimensionOperation> filtered)
    {
        if (string.IsNullOrEmpty(query.RowDimension) || string.IsNullOrEmpty(query.ColumnDimension)
            || string.Equals(query.RowDimension, query.ColumnDimension, StringComparison.Ordinal)
            || !dimensions.Contains(query.RowDimension, StringComparer.Ordinal)
            || !dimensions.Contains(query.ColumnDimension, StringComparer.Ordinal))
            return null;

        static (bool IsUnknown, string Value) ValueFor(DimensionOperation operation, string name) =>
            operation.Variables.TryGetValue(name, out var value)
                ? (false, value)
                : (true, OperationDimensionResolver.UnknownLabel);

        var cells = filtered.GroupBy(operation => (Row: ValueFor(operation, query.RowDimension),
                Column: ValueFor(operation, query.ColumnDimension)))
            .Select(group => new DimensionMatrixCell(
                OperationDimensionResolver.ResolveValue(query.RowDimension,
                    group.Key.Row.IsUnknown ? null : group.Key.Row.Value),
                OperationDimensionResolver.ResolveValue(query.ColumnDimension,
                    group.Key.Column.IsUnknown ? null : group.Key.Column.Value),
                AggregateDimensionMetrics(group)))
            .OrderBy(cell => cell.Row.IsUnknown ? 1 : 0)
            .ThenBy(cell => cell.Row.Value, StringComparer.Ordinal)
            .ThenBy(cell => cell.Column.IsUnknown ? 1 : 0)
            .ThenBy(cell => cell.Column.Value, StringComparer.Ordinal)
            .ToArray();

        return new DimensionMatrixStats(query.RowDimension, query.ColumnDimension, cells,
            AggregateDimensionMetrics(filtered));
    }

    private sealed record DimensionOperation(
        string? Profile, string? Engine, string? Server, string? Database, string Status, long? DurationMs,
        string? VariablesJson, IReadOnlyDictionary<string, string> Variables)
    {
        public IReadOnlyList<string> VariableNames => OperationDimensionResolver.ReadDimensionNames(VariablesJson);
    }

    /// <summary>Keyset page of operations after (updatedAt, id), oldest first, on the caller's snapshot.</summary>
    public IReadOnlyList<OperationSummary> OperationsUpdatedAfter(SqliteConnection connection, string updatedAt, long id, int limit)
    {
        var liveness = new ProcessLiveness(processes, _dead);
        return Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE o.updated_at >= $at AND (o.updated_at > $at OR o.id > $id) ORDER BY o.updated_at, o.id LIMIT $limit;
            """, [("$at", updatedAt), ("$id", id), ("$limit", limit)], reader => ReadOperation(reader, liveness));
    }

    /// <summary>Keyset page of sessions after (lastSeen, id), oldest first, on the caller's snapshot.</summary>
    public IReadOnlyList<SessionSummary> SessionsSeenAfter(SqliteConnection connection, string lastSeen, long id, int limit) =>
        WithRunning(connection, Query(connection, $"""
            SELECT {SessionColumns} FROM sessions s
            WHERE s.last_seen >= $at AND (s.last_seen > $at OR s.id > $id) ORDER BY s.last_seen, s.id LIMIT $limit;
            """, [("$at", lastSeen), ("$id", id), ("$limit", limit)], ReadSessionRow).ToArray());

    public OperationSummary? OperationById(SqliteConnection connection, long id)
    {
        var liveness = new ProcessLiveness(processes, _dead);
        return Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id WHERE o.id = $id;
            """, [("$id", id)], reader => ReadOperation(reader, liveness)).FirstOrDefault();
    }

    public SessionSummary? SessionById(SqliteConnection connection, long id) =>
        WithRunning(connection, Query(connection, $"SELECT {SessionColumns} FROM sessions s WHERE s.id = $id;",
            [("$id", id)], ReadSessionRow).ToArray()).FirstOrDefault();

    public IReadOnlyList<OperationSummary> RunningOperations(SqliteConnection connection)
    {
        var liveness = new ProcessLiveness(processes, _dead);
        return Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE o.status = 'running' ORDER BY o.id;
            """, [], reader => ReadOperation(reader, liveness));
    }

    internal static string? MaxTimestamp(SqliteConnection connection) =>
        Scalar<string?>(connection, "SELECT MAX(t) FROM (SELECT MAX(updated_at) AS t FROM operations UNION ALL SELECT MAX(last_seen) FROM sessions);", []);

    private IReadOnlyList<VariantDetail> Variants(SqliteConnection connection, long operationId)
    {
        var rows = new List<VariantDetail>();
        foreach (var metric in Query(connection, """
            SELECT id, ordinal, variant, parameter_set, matrix_cell, runs,
                elapsed_ms_min, elapsed_ms_median, elapsed_ms_max, cpu_ms_min, cpu_ms_median, cpu_ms_max,
                logical_reads_min, logical_reads_median, logical_reads_max,
                grant_requested_kb, grant_granted_kb, grant_max_used_kb, dop, compile_time_ms, compile_cpu_ms,
                spill_count, has_warnings, has_implicit_conversion, missing_index_count, waits_json,
                pg_shared_hit, pg_shared_read, pg_shared_dirtied, pg_shared_written, pg_temp_read, pg_temp_written
            FROM operation_metrics WHERE operation_id = $id ORDER BY ordinal;
            """, [("$id", operationId)], r => (Id: r.GetInt64(0), Detail: ReadVariant(r))))
        {
            var tableIo = Query(connection, """
                SELECT table_name, logical_reads, scan_count, physical_reads, page_server_reads, read_ahead_reads,
                    lob_logical_reads, lob_physical_reads, lob_read_ahead_reads, cold_runs
                FROM operation_table_io WHERE metric_id = $id ORDER BY logical_reads DESC, table_name;
                """, [("$id", metric.Id)], r => new TableIoRow(
                    r.GetString(0), r.GetInt64(1), NullableLong(r, 2), NullableLong(r, 3), NullableLong(r, 4), NullableLong(r, 5),
                    NullableLong(r, 6), NullableLong(r, 7), NullableLong(r, 8), r.GetInt32(9)));
            var plans = Query(connection, """
                SELECT p.repetition, p.ordinal, p.plan_hash, EXISTS (SELECT 1 FROM plans s WHERE s.hash = p.plan_hash)
                FROM operation_plans p WHERE p.metric_id = $id ORDER BY p.repetition, p.ordinal;
                """, [("$id", metric.Id)], r => new PlanLinkRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3) != 0));
            rows.Add(metric.Detail with { TableIo = tableIo, Plans = plans });
        }

        return rows;
    }

    private SessionSummary[] WithRunning(SqliteConnection connection, SessionSummary[] sessions)
    {
        if (sessions.Length == 0)
            return sessions;
        var liveness = new ProcessLiveness(processes, _dead);
        var ids = string.Join(",", sessions.Select(s => s.Id.ToString(CultureInfo.InvariantCulture)));
        var running = Query(connection, $"""
            SELECT session_id, host_pid, host_started_at FROM operations WHERE status = 'running' AND session_id IN ({ids});
            """, [], r => (Session: r.GetInt64(0), Status: liveness.Status("running", r.GetInt64(1), NullableString(r, 2))));
        return sessions.Select(s => s with
        {
            Running = running.Count(r => r.Session == s.Id && r.Status == "running"),
            Abandoned = running.Count(r => r.Session == s.Id && r.Status == "abandoned"),
        }).ToArray();
    }

    private static SessionSummary ReadSessionRow(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), NullableString(r, 5),
        NullableString(r, 6), NullableString(r, 7), NullableString(r, 8), r.GetString(9), r.GetString(10),
        r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), 0, 0);

    private static OperationSummary ReadOperation(SqliteDataReader r, ProcessLiveness liveness) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        liveness.Status(r.GetString(4), r.GetInt64(19), NullableString(r, 20)),
        r.IsDBNull(5) ? null : r.GetInt32(5), NullableString(r, 6), r.GetString(7), r.GetString(8), NullableString(r, 9),
        NullableLong(r, 10), NullableString(r, 11), NullableString(r, 12), NullableString(r, 13), NullableString(r, 14),
        r.GetInt64(15) != 0, NullableString(r, 16), NullableLong(r, 17), NullableLong(r, 21),
        r.GetInt64(22) != 0, r.GetInt64(23) != 0, r.GetInt64(24) != 0, Json(NullableString(r, 18)),
        NullableString(r, 25));

    private static VariantDetail ReadVariant(SqliteDataReader r) => new(
        r.GetInt32(1), r.GetString(2), NullableString(r, 3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetInt32(5),
        SpreadAt(r, 6), SpreadAt(r, 9), SpreadAt(r, 12),
        NullableLong(r, 15), NullableLong(r, 16), NullableLong(r, 17), r.IsDBNull(18) ? null : r.GetInt32(18),
        NullableLong(r, 19), NullableLong(r, 20),
        r.GetInt32(21), r.GetInt64(22) != 0, r.GetInt64(23) != 0, r.GetInt32(24),
        Json(NullableString(r, 25)),
        r.IsDBNull(26) ? null : new PostgresBuffers(NullableLong(r, 26), NullableLong(r, 27), NullableLong(r, 28),
            NullableLong(r, 29), NullableLong(r, 30), NullableLong(r, 31)),
        [], []);

    private static SqlHashStat ReadSqlStat(SqliteDataReader r) =>
        new(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3));

    private static Spread? SpreadAt(SqliteDataReader r, int index) =>
        r.IsDBNull(index) ? null : new Spread(r.GetInt64(index), r.GetInt64(index + 1), r.GetInt64(index + 2));

    /// <summary>A stored JSON value, or null when it is absent or malformed, so one bad row never fails a response.</summary>
    private static JsonElement? Json(string? text)
    {
        if (text is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? Vars(string? text)
    {
        if (text is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NullableString(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetString(index);

    private static long? NullableLong(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetInt64(index);

    private static int Clamp(int limit) => Math.Clamp(limit, 1, MaximumLimit);

    private static string? Iso(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static List<T> Query<T>(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters, Func<SqliteDataReader, T> read)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(read(reader));
        return rows;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }
}