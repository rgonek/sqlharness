using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Dashboard.Transcripts;

namespace SqlHarness.Dashboard;

/// <summary>
/// Read-only queries over activity.db. Every call opens its own short-lived,
/// unpooled read-only connection; a missing database reads as empty. Running
/// rows are reported as <c>abandoned</c> when their host process is gone.
/// </summary>
public sealed class JournalReader(string databasePath, IProcessInfo processes)
{
    private sealed record OptimizationEvidenceRow(
        long OperationId, string Operation, string? Subject, string? Scope, string? Database, string? Engine, string? Fingerprint,
        string? MeasurementFingerprint, string? BaselineHash, string? CandidateHash, string StartedAt, string Status, string? ConditionsJson, string? SummaryJson,
        long? MetricId, string? Variant, string? CaseId, string? CaseLabel, long? LogicalReadsMedian, long? ElapsedMedianMs,
        long? CpuMedianMs, long? PgSharedHit, long? PgSharedRead, long? PgSharedDirtied, long? PgSharedWritten,
        long? PgTempRead, long? PgTempWritten,
        string? Table, long? TableLogicalReads, long? TableLobLogicalReads, long? ScanCount, int OmittedStatements);

    private sealed record StoredStatement(long Id, long OperationId, string Variant, string? ParameterSet, string CaseId, int Ordinal, string Key,
        int Occurrence, string Hash, string? Text, long? Executions, string? PlanHash, string Status,
        double? UdfElapsedMs, string HiddenWorkJson, string? ObjectName, string? ObjectKind,
        long? ObjectReads, long? ObjectLobReads, long? ObjectScans, int OmittedStatements,
        int? PlanRepetition, int? PlanDocumentOrdinal, int? PlanStatementIndex,
        long? CpuMs, long? ElapsedMs, int? EvidenceRepetition, bool EvidenceEligible);

    private static readonly JsonSerializerOptions ConditionsJsonOptions = new(JsonSerializerDefaults.Web);
    public const int DefaultLimit = 50;
    internal const int MaximumSearchLength = 200;
    public const int MaximumLimit = 200;
    public const int SessionOperationLimit = 500;
    public const int AgentSessionOperationPageLimit = 500;
    public const int CompareHistoryPageLimit = 100;
    public const int MutationHistoryPageLimit = 100;
    public const int TopLimit = 20;
    internal const int MaximumSqlVersionBytes = 256 * 1024;
    internal const int MaximumStoredPlanBytes = 16 * 1024 * 1024;
    internal const long OverGrantMinimumKb = 1024;

    // Shared across reads and threads (live feed ticks, API requests): dead hosts stay dead.
    private readonly DeadProcesses _dead = new();

    // Narrow test seam for proving selected evidence ids are fetched as bounded batches.
    internal Action<IReadOnlyList<long>>? CompareEvidenceQueryObserver { get; init; }

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
        o.error_message, o.cancel_reason,
        COALESCE(o.agent_model, (SELECT l.model FROM operation_agent_links l WHERE l.operation_id = o.id)) AS agent_model,
        COALESCE(o.agent_reasoning_effort, (SELECT l.effort FROM operation_agent_links l WHERE l.operation_id = o.id)) AS agent_reasoning_effort,
        o.operation_ref, o.parent_operation_ref, o.dry_run,
        CASE WHEN o.agent_model IS NOT NULL THEN 'call'
             WHEN EXISTS (SELECT 1 FROM operation_agent_links l WHERE l.operation_id = o.id AND l.model IS NOT NULL) THEN 'transcript'
        END AS agent_model_source,
        o.compare_subject,
        (SELECT SUM(m.logical_reads_median) FROM operation_metrics m WHERE m.operation_id = o.id AND lower(m.variant) = 'baseline') AS reads_before,
        (SELECT SUM(m.logical_reads_median) FROM operation_metrics m WHERE m.operation_id = o.id AND lower(m.variant) = 'candidate') AS reads_after,
        o.warnings_json,
        (SELECT agent_label_initial FROM operation_annotations a WHERE a.operation_id = o.id) AS agent_label_initial,
        (SELECT agent_label FROM operation_annotations a WHERE a.operation_id = o.id) AS agent_label,
        COALESCE((SELECT agent_keep FROM operation_annotations a WHERE a.operation_id = o.id), 0) AS agent_keep,
        COALESCE((SELECT owner_starred FROM operation_annotations a WHERE a.operation_id = o.id), 0) AS owner_starred,
        (SELECT owner_note FROM operation_annotations a WHERE a.operation_id = o.id) AS owner_note,
        s.dashboard_evidence_version
        """;

    private const string SessionColumns = """
        s.id, s.session_key, s.agent_kind, s.transport, s.source, s.client_name, s.client_version, s.mcp_mode, s.cwd,
        s.first_seen, s.last_seen,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id) AS ops,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id AND o.status = 'failed') AS failed,
        (SELECT COUNT(*) FROM operations o WHERE o.session_id = s.id AND o.status = 'rejected') AS rejected,
        s.client_title, s.roots_json, s.protocol_version,
        (SELECT a.title FROM session_annotations a WHERE a.session_id = s.id) AS title,
        COALESCE((SELECT a.starred FROM session_annotations a WHERE a.session_id = s.id), 0) AS starred,
        EXISTS (SELECT 1 FROM session_annotations a WHERE a.session_id = s.id AND a.archived_at IS NOT NULL) AS archived,
        (SELECT substr(rtrim(CASE WHEN instr(n.note, char(10)) > 0 THEN substr(n.note, 1, instr(n.note, char(10)) - 1) ELSE n.note END, char(13)), 1, 120)
         FROM (SELECT ltrim(a.note, char(10, 13, 32, 9)) AS note FROM session_annotations a WHERE a.session_id = s.id) n) AS note_preview,
        (SELECT c.title FROM operation_agent_links l
         JOIN agent_conversations c ON c.id = l.conversation
         JOIN operations o2 ON o2.id = l.operation_id
         WHERE o2.session_id = s.id AND c.title IS NOT NULL
         ORDER BY o2.id DESC LIMIT 1) AS agent_title,
        EXISTS (SELECT 1 FROM operations compare_op WHERE compare_op.session_id = s.id
                AND (compare_op.operation = 'compare' OR (compare_op.operation = 'measure'
                    AND compare_op.compare_subject IS NOT NULL AND compare_op.compare_scope IS NOT NULL))) AS has_compare
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

    public IReadOnlyList<KeyCount> AgentKinds()
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return [];
        return Query(connection, "SELECT agent_kind, COUNT(*) FROM sessions GROUP BY agent_kind ORDER BY agent_kind;", [],
            r => new KeyCount(r.GetString(0), r.GetInt32(1)));
    }

    public Page<SessionSummary> Sessions(SessionQuery query) => ReadSessions(query, unmatchedAgentSessionsOnly: false);

    public Page<SessionSummary> UnmatchedAgentSessions(SessionQuery query) => ReadSessions(query, unmatchedAgentSessionsOnly: true);

    private Page<SessionSummary> ReadSessions(SessionQuery query, bool unmatchedAgentSessionsOnly)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new Page<SessionSummary>([], null);
        var matchedSessionIds = unmatchedAgentSessionsOnly
            ? ReadAgentMemberships(connection).Where(item => item.Key is not null && item.Membership is Membership.Exact or Membership.Inferred)
                .Select(item => item.SessionId).ToHashSet()
            : null;
        if (matchedSessionIds is not null)
            connection.CreateFunction<long, bool>("sqlh_agent_session_matched", id => matchedSessionIds.Contains(id), isDeterministic: true);
        // SQLite resolves the name when the statement is prepared, including when $q is null.
        connection.CreateFunction<string?, string?>("sqlh_fold", FoldForSearch, isDeterministic: true);
        var limit = Clamp(query.Limit);
        (string, object?)[] parameters =
        [
            ("$agent", query.Agent),
            ("$transport", query.Transport),
            ("$from", Iso(query.From)),
            ("$to", Iso(query.To)),
            ("$cursor", query.Cursor),
            ("$limit", limit + 1),
            ("$archived", query.Archived is null ? null : query.Archived.Value ? 1 : 0),
            ("$starred", query.Starred is null ? null : query.Starred.Value ? 1 : 0),
            ("$hasCompare", query.HasCompare is null ? null : query.HasCompare.Value ? 1 : 0),
            ("$q", query.Search is null ? null : "%" + EscapeLike(FoldForSearch(query.Search)!) + "%"),
        ];
        var order = query.Starred is null ? "starred DESC, s.id DESC" : "s.id DESC";
        var unmatchedOnlyFilter = unmatchedAgentSessionsOnly ? "AND sqlh_agent_session_matched(s.id) = 0" : "";
        var rows = Query(connection, $"""
            SELECT {SessionColumns} FROM sessions s
            WHERE ($agent IS NULL OR s.agent_kind = $agent)
              AND ($transport IS NULL OR s.transport = $transport)
              AND ($from IS NULL OR s.last_seen >= $from)
              AND ($to IS NULL OR s.first_seen < $to)
              AND ($cursor IS NULL OR s.id < $cursor)
              AND ($archived IS NULL OR EXISTS (SELECT 1 FROM session_annotations a WHERE a.session_id = s.id AND a.archived_at IS NOT NULL) = $archived)
              AND ($starred IS NULL OR COALESCE((SELECT a.starred FROM session_annotations a WHERE a.session_id = s.id), 0) = $starred)
              AND ($hasCompare IS NULL OR EXISTS (SELECT 1 FROM operations compare_op WHERE compare_op.session_id = s.id
                  AND (compare_op.operation = 'compare' OR (compare_op.operation = 'measure'
                      AND compare_op.compare_subject IS NOT NULL AND compare_op.compare_scope IS NOT NULL))) = $hasCompare)
              {unmatchedOnlyFilter}
              AND ($q IS NULL
                   OR sqlh_fold(s.session_key) LIKE $q ESCAPE '\' OR sqlh_fold(s.cwd) LIKE $q ESCAPE '\'
                   OR sqlh_fold(s.client_title) LIKE $q ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM session_annotations a WHERE a.session_id = s.id
                              AND (sqlh_fold(a.title) LIKE $q ESCAPE '\' OR sqlh_fold(a.note) LIKE $q ESCAPE '\'))
                   OR EXISTS (SELECT 1 FROM operation_agent_links l JOIN agent_conversations c ON c.id = l.conversation
                              JOIN operations o3 ON o3.id = l.operation_id
                              WHERE o3.session_id = s.id AND sqlh_fold(c.title) LIKE $q ESCAPE '\'))
            ORDER BY {order} LIMIT $limit;
            """,
            parameters,
            ReadSessionRow);
        var page = rows.Take(limit).ToArray();
        return new Page<SessionSummary>(WithOptimizationBadges(WithRunning(connection, page)),
            query.Starred is not null && rows.Count > limit ? page[^1].Id : null);
    }

    /// <summary>Reads current operation identities and cached transcript links, then derives one membership per operation.</summary>
    internal IReadOnlyList<OperationMembership> AgentSessionMemberships()
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return [];

        var operations = Query(connection, """
            SELECT o.id, o.session_id, s.agent_kind, s.transport, s.agent_pid, s.agent_started_at,
                   o.started_at, o.agent_session_id, o.agent_thread_source, o.updated_at
            FROM operations o JOIN sessions s ON s.id = o.session_id
            ORDER BY o.id;
            """, [], reader => new OperationIdentityRow(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                NullableLong(reader, 4), NullableString(reader, 5), reader.GetString(6),
                NullableString(reader, 7), NullableString(reader, 8), reader.GetString(9)));
        var links = Query(connection, """
            SELECT l.operation_id, c.agent_kind, c.conversation_id
            FROM operation_agent_links l JOIN agent_conversations c ON c.id = l.conversation
            ORDER BY l.operation_id;
            """, [], reader => new LinkRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return AgentSessionMembership.Resolve(operations, links);
    }

    internal IReadOnlyList<TranscriptCoverage> AgentSessionCoverage()
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return [];
        var memberships = ReadAgentMemberships(connection).ToDictionary(item => item.OperationId);
        var rows = Query(connection, "SELECT o.id, s.agent_kind, s.transport FROM operations o JOIN sessions s ON s.id = o.session_id;", [],
            reader => (Id: reader.GetInt64(0), Kind: reader.GetString(1), Transport: reader.GetString(2)));
        return rows.GroupBy(row => (row.Kind, row.Transport))
            .Select(group => new TranscriptCoverage(group.Key.Kind, group.Key.Transport,
                group.Count(row => memberships.TryGetValue(row.Id, out var membership) && membership.Membership == Membership.Exact),
                group.Count(row => memberships.TryGetValue(row.Id, out var membership) && membership.Membership == Membership.Inferred),
                group.Count(row => memberships.TryGetValue(row.Id, out var membership) && membership.Membership == Membership.Unassigned)))
            .OrderBy(row => row.AgentKind, StringComparer.Ordinal).ThenBy(row => row.Transport, StringComparer.Ordinal).ToArray();
    }

    public Page<AgentSessionSummary> AgentSessions(bool? archived = false, bool? starred = null, long? cursor = null, int? limit = null,
        string? agent = null, string? q = null, bool? hasCompare = null)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new Page<AgentSessionSummary>([], null);
        var memberships = ReadAgentMemberships(connection);
        var keys = Query(connection, """
            SELECT c.agent_kind, c.conversation_id, c.title, c.id FROM agent_conversations c
            UNION SELECT a.agent_kind, a.conversation_id, NULL, NULL FROM agent_session_annotations a
            UNION SELECT s.agent_kind, o.agent_session_id, NULL, NULL FROM operations o JOIN sessions s ON s.id = o.session_id
                  WHERE o.agent_session_id IS NOT NULL
            ORDER BY 1, 2;
            """, [], r => new AgentSessionKey(r.GetString(0), r.GetString(1))).Distinct().ToArray();
        var rows = new List<AgentSessionSummary>();
        foreach (var key in keys)
        {
            var summary = BuildAgentSessionSummary(connection, memberships, key);
            if (archived is { } archivedValue && summary.Archived != archivedValue || starred is { } starredValue && summary.Starred != starredValue)
                continue;
            if (!string.IsNullOrWhiteSpace(agent) && !string.Equals(summary.Key.AgentKind, agent, StringComparison.Ordinal))
                continue;
            if (hasCompare is { } compareFilter && summary.Children.Any(child => child.Session.HasCompare) != compareFilter)
                continue;
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                var found = new[] { summary.Title, summary.Key.ConversationId, summary.NotePreview }
                    .Concat(summary.Children.SelectMany(child => new[] { child.Session.SessionKey, child.Session.Title, child.Session.ClientTitle, child.Session.Cwd }))
                    .Any(value => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
                if (!found)
                    continue;
            }
            rows.Add(summary);
        }
        var ordered = rows.OrderByDescending(row => row.LastSeen, StringComparer.Ordinal).ThenBy(row => row.Key.AgentKind, StringComparer.Ordinal)
            .ThenBy(row => row.Key.ConversationId, StringComparer.Ordinal).ToArray();
        var pageLimit = Clamp(limit ?? DefaultLimit);
        var offset = (int)Math.Clamp(cursor ?? 0, 0, ordered.Length);
        var page = ordered.Skip(offset).Take(pageLimit + 1).ToArray();
        return new Page<AgentSessionSummary>(page.Take(pageLimit).ToArray(), page.Length > pageLimit ? offset + pageLimit : null);
    }

    public AgentSessionDetail? AgentSession(AgentSessionKey key)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        var memberships = ReadAgentMemberships(connection);
        var exists = Scalar<long>(connection, """
            SELECT EXISTS (SELECT 1 FROM agent_conversations WHERE agent_kind = $kind AND conversation_id = $id)
                OR EXISTS (SELECT 1 FROM agent_session_annotations WHERE agent_kind = $kind AND conversation_id = $id)
                OR EXISTS (SELECT 1 FROM operations o JOIN sessions s ON s.id = o.session_id
                           WHERE s.agent_kind = $kind AND o.agent_session_id = $id);
            """, [("$kind", key.AgentKind), ("$id", key.ConversationId)]) != 0;
        if (!exists)
            return null;
        var matching = memberships.Where(item => item.Key == key).ToArray();
        var childIds = matching.Select(item => item.SessionId).Distinct().ToHashSet();
        var operationPage = SelectAgentSessionOperations(connection, memberships, key, AgentSessionOperationPageLimit, null);
        var children = new List<AgentSessionChild>();
        foreach (var childId in childIds)
        {
            var detail = Session(childId);
            if (detail is null)
                continue;
            var byId = memberships.Where(item => item.SessionId == childId
                && (item.Key == key || item.Membership == Membership.Unassigned)).ToArray();
            var counts = byId.GroupBy(item => item.Membership).ToDictionary(group => group.Key, group => group.Count());
            children.Add(new AgentSessionChild(detail.Session,
                counts.GetValueOrDefault(Membership.Exact), counts.GetValueOrDefault(Membership.Inferred),
                counts.GetValueOrDefault(Membership.Unassigned)));
        }
        var summary = BuildAgentSessionSummary(connection, memberships, key);
        var note = Query(connection, "SELECT note FROM agent_session_annotations WHERE agent_kind = $kind AND conversation_id = $id;",
            [("$kind", key.AgentKind), ("$id", key.ConversationId)], r => NullableString(r, 0)).FirstOrDefault();
        return new AgentSessionDetail(summary with { Children = children.OrderByDescending(child => child.Session.LastSeen, StringComparer.Ordinal).ToArray() },
            operationPage.Items, note);
    }

    public Page<AgentSessionOperation>? AgentSessionOperations(AgentSessionKey key, int? limit = null, long? cursor = null)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        var memberships = ReadAgentMemberships(connection);
        var exists = Scalar<long>(connection, """
            SELECT EXISTS (SELECT 1 FROM agent_conversations WHERE agent_kind = $kind AND conversation_id = $id)
                OR EXISTS (SELECT 1 FROM agent_session_annotations WHERE agent_kind = $kind AND conversation_id = $id)
                OR EXISTS (SELECT 1 FROM operations o JOIN sessions s ON s.id = o.session_id
                           WHERE s.agent_kind = $kind AND o.agent_session_id = $id);
            """, [("$kind", key.AgentKind), ("$id", key.ConversationId)]) != 0;
        if (!exists && !memberships.Any(item => item.Key == key))
            return null;
        return SelectAgentSessionOperations(connection, memberships, key,
            Math.Clamp(limit ?? AgentSessionOperationPageLimit, 1, AgentSessionOperationPageLimit), cursor);
    }

    private Page<AgentSessionOperation> SelectAgentSessionOperations(SqliteConnection connection,
        IReadOnlyList<OperationMembership> memberships, AgentSessionKey key, int limit, long? cursor)
    {
        var childIds = memberships.Where(item => item.Key == key
                && item.Membership is Membership.Exact or Membership.Inferred)
            .Select(item => item.SessionId).ToHashSet();
        var membershipById = memberships.Where(item =>
                (item.Key == key && item.Membership is Membership.Exact or Membership.Inferred)
                || (item.Membership == Membership.Unassigned && childIds.Contains(item.SessionId)))
            .GroupBy(item => item.OperationId)
            .ToDictionary(group => group.Key, group => group.First().Membership);
        var operationIds = membershipById.Keys.Where(id => cursor is null || id < cursor.Value)
            .OrderByDescending(id => id).Take(limit + 1).ToArray();
        var hasMore = operationIds.Length > limit;
        var pageIds = operationIds.Take(limit).ToArray();
        if (pageIds.Length == 0)
            return new Page<AgentSessionOperation>([], null);

        var parameters = pageIds.Select((id, index) => ($"$operation{index}", (object?)id)).ToArray();
        var operationFilter = $"o.id IN ({string.Join(",", pageIds.Select((_, index) => $"$operation{index}"))})";
        var liveness = new ProcessLiveness(processes, _dead);
        var operations = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE {operationFilter} ORDER BY o.id DESC;
            """, parameters, reader => ReadOperation(reader, liveness));
        var items = operations.Select(operation => new AgentSessionOperation(operation,
            membershipById[operation.Id].ToString().ToLowerInvariant())).ToArray();
        return new Page<AgentSessionOperation>(items, hasMore && items.Length > 0 ? items[^1].Operation.Id : null);
    }

    private static AgentSessionSummary BuildAgentSessionSummary(SqliteConnection connection,
        IReadOnlyList<OperationMembership> memberships, AgentSessionKey key)
    {
        var annotation = Query(connection, "SELECT title, note, starred, archived FROM agent_session_annotations WHERE agent_kind = $kind AND conversation_id = $id;",
            [("$kind", key.AgentKind), ("$id", key.ConversationId)], r => new SessionAnnotation(
                NullableString(r, 0), NullableString(r, 1), r.GetInt64(2) != 0, r.GetInt64(3) != 0)).FirstOrDefault()
            ?? new SessionAnnotation(null, null, false, false);
        var relevant = memberships.Where(item => item.Key == key).ToArray();
        var sessionIds = relevant.Select(item => item.SessionId).Distinct().ToHashSet();
        var childCounts = sessionIds.ToDictionary(id => id, id => (
            Exact: relevant.Count(item => item.SessionId == id && item.Membership == Membership.Exact),
            Inferred: relevant.Count(item => item.SessionId == id && item.Membership == Membership.Inferred),
            Unassigned: memberships.Count(item => item.SessionId == id && item.Membership == Membership.Unassigned)));
        var children = sessionIds.Select(id => Query(connection, $"SELECT {SessionColumns} FROM sessions s WHERE s.id = $id;",
                [("$id", id)], ReadSessionRow).FirstOrDefault())
            .Where(session => session is not null).Select(session =>
            {
                var counts = childCounts[session!.Id];
                return new AgentSessionChild(session, counts.Exact, counts.Inferred, counts.Unassigned);
            }).OrderByDescending(child => child.Session.LastSeen, StringComparer.Ordinal).ToArray();
        var conversation = Query(connection, "SELECT title FROM agent_conversations WHERE agent_kind = $kind AND conversation_id = $id;",
            [("$kind", key.AgentKind), ("$id", key.ConversationId)], r => NullableString(r, 0)).FirstOrDefault();
        var newestChildTitle = children.Select(child => child.Session.Title ?? child.Session.ClientTitle).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
        var sourceIsSubagent = Scalar<long>(connection, """
            SELECT COUNT(*) FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE s.agent_kind = $kind AND o.agent_session_id = $id AND lower(COALESCE(o.agent_thread_source, '')) = 'subagent';
            """, [("$kind", key.AgentKind), ("$id", key.ConversationId)]) > 0;
        var title = annotation.Title ?? conversation ?? newestChildTitle ?? (sourceIsSubagent ? "subagent of unknown parent" : null);
        var lastSeen = relevant.Select(item => item.UpdatedAt).OrderByDescending(value => value, StringComparer.Ordinal).FirstOrDefault() ?? "";
        return new AgentSessionSummary(key, title, conversation, annotation.Title,
            PreviewNote(annotation.Note), annotation.Starred, annotation.Archived,
            relevant.Count(item => item.Membership == Membership.Exact),
            relevant.Count(item => item.Membership == Membership.Inferred),
            sessionIds.Sum(id => memberships.Count(item => item.SessionId == id && item.Membership == Membership.Unassigned)), lastSeen, children);
    }

    private static IReadOnlyList<OperationMembership> ReadAgentMemberships(SqliteConnection connection)
    {
        var operations = Query(connection, """
            SELECT o.id, o.session_id, s.agent_kind, s.transport, s.agent_pid, s.agent_started_at,
                   o.started_at, o.agent_session_id, o.agent_thread_source, o.updated_at
            FROM operations o JOIN sessions s ON s.id = o.session_id ORDER BY o.id;
            """, [], reader => new OperationIdentityRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), NullableLong(reader, 4), NullableString(reader, 5), reader.GetString(6),
                NullableString(reader, 7), NullableString(reader, 8), reader.GetString(9)));
        var links = Query(connection, """
            SELECT l.operation_id, c.agent_kind, c.conversation_id FROM operation_agent_links l
            JOIN agent_conversations c ON c.id = l.conversation ORDER BY l.operation_id;
            """, [], reader => new LinkRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return AgentSessionMembership.Resolve(operations, links);
    }

    private static string? PreviewNote(string? note)
    {
        if (note is null) return null;
        var line = note.Split(['\r', '\n'], 2)[0].Trim();
        return line.Length > 120 ? line[..120] : line;
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
        var compares = CompareHistoryBuilder.Build(CompareHistoryRows(connection, id));
        var subjects = CompareHistoryBuilder.Subjects(compares);
        var selectedEvidenceIds = subjects.SelectMany(subject => new[]
                { subject.LatestWhole?.OperationId, subject.LatestCompletedWhole?.OperationId, subject.StartToLatestWhole?.OperationId })
            .Where(operationId => operationId is not null).Select(operationId => operationId!.Value).ToHashSet();
        var evidence = HydrateCompareEvidence(connection, compares.Where(attempt => selectedEvidenceIds.Contains(attempt.OperationId)).ToArray());
        var evidenceById = evidence.ToDictionary(attempt => attempt.OperationId);
        subjects = CompareHistoryBuilder.Subjects(compares.Select(attempt =>
            evidenceById.TryGetValue(attempt.OperationId, out var hydrated) ? hydrated : attempt).ToArray());
        var note = Query(connection, "SELECT note FROM session_annotations WHERE session_id = $id;", [("$id", id)],
            r => NullableString(r, 0)).FirstOrDefault();
        var conversations = Query(connection, """
            SELECT c.agent_kind, c.conversation_id, c.title, COUNT(*) FROM operation_agent_links l
            JOIN agent_conversations c ON c.id = l.conversation JOIN operations o ON o.id = l.operation_id
            WHERE o.session_id = $id GROUP BY c.id ORDER BY MAX(o.id) DESC;
            """, [("$id", id)], r => new AgentConversation(r.GetString(0), r.GetString(1), NullableString(r, 2), r.GetInt32(3)));
        return new SessionDetail(WithRunning(connection, [session])[0], operations,
            subjects,
            compares.Count(attempt => attempt.Subject is null || attempt.Scope is null), note, conversations);
    }

    public Page<OperationSummary>? SessionOperations(long sessionId, int? limit = null, long? cursor = null)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        if (Scalar<long>(connection, "SELECT EXISTS (SELECT 1 FROM sessions WHERE id = $id);", [("$id", sessionId)]) == 0)
            return null;

        var pageLimit = Math.Clamp(limit ?? SessionOperationLimit, 1, SessionOperationLimit);
        var liveness = new ProcessLiveness(processes, _dead);
        var rows = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE o.session_id = $session AND ($cursor IS NULL OR o.id < $cursor)
            ORDER BY o.id DESC LIMIT $limit;
            """, [("$session", sessionId), ("$cursor", cursor), ("$limit", pageLimit + 1)],
            reader => ReadOperation(reader, liveness));
        var items = rows.Take(pageLimit).ToArray();
        long? nextCursor = rows.Count > pageLimit && items.Length > 0 ? items[^1].Id : null;
        return new Page<OperationSummary>(items, nextCursor);
    }

    /// <summary>Builds the optimization section from every retained compare and its journaled case evidence.</summary>
    public OptimizationSection Optimization(long sessionId, bool storeSensitive = true) =>
        OptimizationSummaryBuilder.Build(OptimizationEvidence(sessionId, storeSensitive));

    public OptimizationSection AgentOptimization(AgentSessionKey key, bool storeSensitive = true)
    {
        var evidence = OptimizationEvidenceForAgentSession(key, storeSensitive);
        var context = ContextKey.AgentSession(key.AgentKind, key.ConversationId);
        var labels = CompareVersionLabels(context);
        var hashes = evidence.Where(item => item.Subject is not null && item.Scope is not null)
            .SelectMany(item => new[] { item.BaselineHash, item.CandidateHash }.Where(hash => hash is not null)
                .Select(hash => (item.Subject!, item.Scope!, hash!))).Distinct().ToArray();
        var provisional = hashes.Any(hash => !labels.ContainsKey(hash));
        return OptimizationSummaryBuilder.Build(evidence, context, labels, provisional);
    }

    /// <summary>Reads every retained compare with its actual per-case and per-table journal evidence.</summary>
    public IReadOnlyList<CompareEvidence> OptimizationEvidence(long sessionId, bool storeSensitive = true)
        => ReadOptimizationEvidence(sessionId, null, storeSensitive);

    public IReadOnlyList<CompareEvidence> OptimizationEvidenceForAgentSession(AgentSessionKey key, bool storeSensitive = true)
        => ReadOptimizationEvidence(null, key, storeSensitive);

    private IReadOnlyDictionary<(string Subject, string Scope, string Hash), string> CompareVersionLabels(ContextKey context)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new Dictionary<(string Subject, string Scope, string Hash), string>();
        return Query(connection, "SELECT subject, scope, sql_hash, label FROM compare_version_labels WHERE context_kind = $kind AND context_key = $key;",
            [("$kind", context.Kind), ("$key", context.Key)], reader =>
                (Key: (reader.GetString(0), reader.GetString(1), reader.GetString(2)), Label: reader.GetString(3)))
            .ToDictionary(row => row.Key, row => row.Label);
    }

    private IReadOnlyList<CompareEvidence> ReadOptimizationEvidence(long? sessionId, AgentSessionKey? agentSession, bool storeSensitive)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return [];

        var liveness = new ProcessLiveness(processes, _dead);
        var rows = Query(connection, """
            SELECT o.id, o.operation, o.compare_subject, o.compare_scope, o.database, o.engine,
                   o.conditions_fingerprint, o.measurement_fingerprint,
                   o.sql_hash, o.candidate_sql_hash, o.started_at, o.status, o.host_pid, o.host_started_at,
                   o.conditions_json, o.summary_json,
                   m.id, m.variant, m.case_id, m.case_label, m.logical_reads_median, m.elapsed_ms_median,
                   m.cpu_ms_median, m.pg_shared_hit, m.pg_shared_read, m.pg_shared_dirtied,
                   m.pg_shared_written, m.pg_temp_read, m.pg_temp_written,
                   t.table_name, t.logical_reads, t.lob_logical_reads, t.scan_count, COALESCE(m.omitted_statements, 0)
            FROM operations o JOIN sessions s ON s.id = o.session_id
            LEFT JOIN operation_metrics m ON m.operation_id = o.id
            LEFT JOIN operation_table_io t ON t.metric_id = m.id
            WHERE (($session IS NOT NULL AND o.session_id = $session) OR ($agentKind IS NOT NULL AND s.transport = 'mcp'
                  AND s.agent_kind = $agentKind AND (EXISTS (SELECT 1 FROM operation_agent_links l
                      JOIN agent_conversations c ON c.id = l.conversation WHERE l.operation_id = o.id
                      AND c.agent_kind = $agentKind AND c.conversation_id = $conversation)
                    OR (s.agent_kind IN ('codex', 'opencode') AND o.agent_session_id = $conversation))))
              AND (o.operation = 'compare' OR (o.operation = 'measure'
                AND o.compare_subject IS NOT NULL AND o.compare_scope IS NOT NULL))
            ORDER BY o.started_at, o.id, m.ordinal, t.table_name;
            """, [("$session", sessionId), ("$agentKind", agentSession?.AgentKind), ("$conversation", agentSession?.ConversationId)], r => new OptimizationEvidenceRow(
            r.GetInt64(0), r.GetString(1), NullableString(r, 2), NullableString(r, 3), NullableString(r, 4), NullableString(r, 5), NullableString(r, 6),
            NullableString(r, 7), NullableString(r, 8), NullableString(r, 9), r.GetString(10),
            liveness.Status(r.GetString(11), r.GetInt64(12), NullableString(r, 13)), NullableString(r, 14), NullableString(r, 15),
            r.IsDBNull(16) ? null : r.GetInt64(16), NullableString(r, 17), NullableString(r, 18), NullableString(r, 19),
            NullableLong(r, 20), NullableLong(r, 21), NullableLong(r, 22), NullableLong(r, 23), NullableLong(r, 24),
            NullableLong(r, 25), NullableLong(r, 26), NullableLong(r, 27), NullableLong(r, 28), NullableString(r, 29),
            NullableLong(r, 30), NullableLong(r, 31), NullableLong(r, 32), r.GetInt32(33)));

        var statementsByMetric = LoadStoredStatements(connection, rows.Select(row => row.OperationId).Distinct(), storeSensitive)
            .GroupBy(row => (row.OperationId, row.Variant, row.CaseId))
            .ToDictionary(group => group.Key, group => group.ToArray());

        var compares = rows.GroupBy(row => row.OperationId).Select(group =>
        {
            var first = group.First();
            var conditions = DeserializeConditions(first.ConditionsJson);
            var metricRows = group.Where(row => row.MetricId is not null)
                .GroupBy(row => (row.MetricId, row.Variant, row.CaseId, row.CaseLabel))
                .Select(metric =>
                {
                    var samples = metric.ToArray();
                    var tableRows = samples.Where(row => row.Table is not null)
                        .Select(row => new CompareTableIoEvidence(row.Table!, row.TableLogicalReads!.Value, row.TableLobLogicalReads, row.ScanCount))
                        .ToArray();
                    long? scans = tableRows.Length == 0 || tableRows.Any(row => row.Scans is null)
                        ? null
                        : tableRows.Sum(row => row.Scans!.Value);
                    var postgres = samples.Any(row => row.PgSharedHit.HasValue || row.PgSharedRead.HasValue
                        || row.PgSharedDirtied.HasValue || row.PgSharedWritten.HasValue || row.PgTempRead.HasValue
                        || row.PgTempWritten.HasValue)
                        ? new PostgresBuffers(samples[0].PgSharedHit, samples[0].PgSharedRead, samples[0].PgSharedDirtied,
                            samples[0].PgSharedWritten, samples[0].PgTempRead, samples[0].PgTempWritten)
                        : null;
                    var caseFingerprint = conditions?.Cases.FirstOrDefault(item => item.Id == metric.Key.CaseId)?.EffectiveMeasurementFingerprint;
                    var statementRows = statementsByMetric.GetValueOrDefault((first.OperationId, metric.Key.Variant!, metric.Key.CaseId!)) ?? [];
                    var statements = statementRows.GroupBy(row => row.Id).Select(statement =>
                    {
                        var row = statement.First();
                        var objectRows = statement.Where(item => item.ObjectName is not null).Select(item =>
                            new TableRow(item.ObjectName!, item.ObjectScans, item.ObjectScans,
                                item.ObjectReads is { } reads ? SaturatingAdd(reads, item.ObjectLobReads ?? 0) : null,
                                item.ObjectReads is { } afterReads ? SaturatingAdd(afterReads, item.ObjectLobReads ?? 0) : null, "0%")
                            { ObjectKind = item.ObjectKind,
                              LobLogicalReadsBefore = item.EvidenceEligible ? item.ObjectLobReads : null,
                              LobLogicalReadsAfter = item.EvidenceEligible ? item.ObjectLobReads : null }).ToArray();
                        var reads = objectRows.All(item => item.ReadsBefore.HasValue)
                            ? objectRows.Aggregate(0L, (sum, item) => SaturatingAdd(sum, item.ReadsBefore!.Value)) : (long?)null;
                        var scansForStatement = objectRows.All(item => item.ScansBefore.HasValue)
                            ? objectRows.Aggregate(0L, (sum, item) => SaturatingAdd(sum, item.ScansBefore!.Value)) : (long?)null;
                        var text = storeSensitive ? row.Text : null;
                        text ??= $"statement {row.Ordinal + 1} ({row.Hash[..Math.Min(8, row.Hash.Length)]})";
                        IReadOnlyList<HiddenWork> hiddenWork;
                        try { hiddenWork = JsonSerializer.Deserialize<HiddenWork[]>(row.HiddenWorkJson, ConditionsJsonOptions) ?? []; }
                        catch (JsonException) { hiddenWork = []; }
                        return new CompareStatementEvidence(row.Key, row.Occurrence, text, row.PlanHash,
                            row.Status, reads, scansForStatement,
                            row.Executions, hiddenWork, objectRows)
                        { UdfElapsedMilliseconds = row.UdfElapsedMs, PlanLink = PlanLink(row), CpuMs = row.CpuMs, ElapsedMs = row.ElapsedMs, PlanRepetition = row.EvidenceRepetition };
                    }).ToArray();
                    var omittedStatements = samples.Select(row => row.OmittedStatements).DefaultIfEmpty(0).Max();
                    var attribution = statements.Length > 0 && omittedStatements == 0 && statements.All(row => row.Status == "confirmed")
                        ? "confirmed" : statements.Length == 0 && omittedStatements == 0 ? "unavailable" : "partial";
                    return new CompareMetricEvidence(metric.Key.Variant!, metric.Key.CaseId!, metric.Key.CaseLabel!,
                        samples[0].LogicalReadsMedian, samples[0].ElapsedMedianMs, scans, tableRows)
                    {
                        CpuMedianMs = samples[0].CpuMedianMs,
                        Postgres = postgres,
                        MeasurementFingerprint = caseFingerprint,
                        Statements = statements,
                        OmittedStatements = omittedStatements,
                        StatementReads = statements.All(row => row.Reads.HasValue)
                            ? statements.Aggregate(0L, (sum, row) => SaturatingAdd(sum, row.Reads!.Value)) : null,
                        StatementAttribution = attribution,
                    };
                }).ToArray();
            var summary = Json(first.SummaryJson);
            var caseEvidence = ReadOptimizationCases(summary, conditions, metricRows);
            var comparisonMode = StringProperty(summary, "comparison") ?? conditions?.Execution?.ComparisonMode;
            var resultsEquivalent = BooleanProperty(summary, "resultsEquivalent") ?? AggregateEquivalence(caseEvidence);
            return new CompareEvidence(first.OperationId, first.Subject, first.Scope, first.Database, first.Engine,
                first.MeasurementFingerprint ?? conditions?.MeasurementFingerprint ?? first.Fingerprint ?? conditions?.Fingerprint,
                first.BaselineHash, first.Operation == "measure" ? null : first.CandidateHash, first.StartedAt,
                first.Status, comparisonMode, resultsEquivalent,
                StringProperty(Property(summary, "regression"), "verdict"), caseEvidence, metricRows)
            { IsMeasure = first.Operation == "measure" };
        }).ToArray();

        return compares;
    }

    private static IReadOnlyList<StoredStatement> LoadStoredStatements(SqliteConnection connection,
        IEnumerable<long> operationIds, bool storeSensitive)
    {
        var ids = operationIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];
        var schemaVersion = Scalar<long>(connection, "PRAGMA user_version;", []);
        if (schemaVersion < 19)
            return [];
        var result = new List<StoredStatement>();
        var evidenceEligible = schemaVersion >= 22
            ? "sess.dashboard_evidence_version = 1 AND lower(op.engine) = 'sqlserver'" : "0";
        foreach (var batch in ids.Chunk(200))
        {
            var parameters = batch.Select((id, index) => ($"$id{index}", (object?)id)).ToArray();
            var placeholders = string.Join(",", parameters.Select(item => item.Item1));
            var queryParameters = parameters.Append(("$storeSensitive", (object?)(storeSensitive ? 1 : 0))).ToArray();
            result.AddRange(Query(connection, $"""
                SELECT s.id, s.operation_id, s.variant, s.parameter_set, s.case_id, s.statement_ordinal, s.statement_key,
                       s.occurrence, s.statement_hash, s.statement_text, s.executions, s.plan_hash, s.status,
                       s.udf_elapsed_ms, s.hidden_work_json, o.object, o.kind, o.logical_reads, o.lob_logical_reads,
                       o.scans, COALESCE(m.omitted_statements, 0),
                       CASE WHEN $storeSensitive = 1 AND p.document_sha IS NOT NULL THEN p.repetition END,
                       CASE WHEN $storeSensitive = 1 AND p.document_sha IS NOT NULL THEN p.document_ordinal END,
                       CASE WHEN $storeSensitive = 1 AND p.document_sha IS NOT NULL THEN p.statement_index END,
                       CASE WHEN {evidenceEligible} THEN s.cpu_ms END,
                       CASE WHEN {evidenceEligible} THEN s.elapsed_ms END,
                       CASE WHEN {evidenceEligible} THEN s.plan_repetition END,
                       CASE WHEN {evidenceEligible} THEN 1 ELSE 0 END
                FROM operation_statements s
                JOIN operations op ON op.id = s.operation_id
                JOIN sessions sess ON sess.id = op.session_id
                LEFT JOIN operation_statement_objects o ON o.statement_id = s.id
                LEFT JOIN operation_metrics m ON m.operation_id = s.operation_id AND m.variant = s.variant
                    AND m.parameter_set IS s.parameter_set AND m.case_id = s.case_id
                LEFT JOIN operation_metrics pm ON pm.operation_id = s.operation_id AND lower(pm.variant) = lower(s.variant)
                    AND pm.parameter_set IS s.parameter_set AND pm.case_id = s.case_id
                LEFT JOIN plan_occurrences p ON p.metric_id = pm.id AND p.repetition = s.plan_repetition
                    AND p.document_ordinal = s.plan_document_ordinal AND p.statement_index = s.plan_statement_index
                    AND p.omitted = 0 AND p.document_sha IS NOT NULL
                    AND EXISTS (SELECT 1 FROM plan_documents d WHERE d.sha256 = p.document_sha)
                WHERE s.operation_id IN ({placeholders})
                ORDER BY s.operation_id, s.ordinal, o.object;
                """, queryParameters, r => new StoredStatement(r.GetInt64(0), r.GetInt64(1), r.GetString(2), NullableString(r, 3), r.GetString(4),
                r.GetInt32(5), r.GetString(6), r.GetInt32(7), r.GetString(8),
                storeSensitive ? NullableString(r, 9) : null, NullableLong(r, 10), NullableString(r, 11),
                r.GetString(12), NullableDouble(r, 13), r.GetString(14),
                NullableString(r, 15), NullableString(r, 16), NullableLong(r, 17), NullableLong(r, 18),
                NullableLong(r, 19), r.GetInt32(20), NullableInt(r, 21), NullableInt(r, 22), NullableInt(r, 23),
                NullableLong(r, 24), NullableLong(r, 25), NullableInt(r, 26), r.GetInt32(27) == 1)));
        }
        return result;
    }

    private sealed record OperationStatementEvidenceResult(IReadOnlyList<StatementRow> Rows, int OmittedStatements, string Attribution);

    private static OperationStatementEvidenceResult OperationStatements(SqliteConnection connection, long operationId,
        bool storeSensitive)
    {
        var statements = LoadStoredStatements(connection, [operationId], storeSensitive);
        var coverage = Query(connection, """
            SELECT COALESCE(MAX(m.omitted_statements), 0), COUNT(DISTINCT s.id),
                   COALESCE(SUM(CASE WHEN s.id IS NOT NULL AND s.status <> 'confirmed' THEN 1 ELSE 0 END), 0),
                   (SELECT COUNT(*) FROM (SELECT DISTINCT case_id, parameter_set FROM operation_metrics WHERE operation_id = $id))
            FROM operation_metrics m LEFT JOIN operation_statements s ON s.operation_id = m.operation_id
            WHERE m.operation_id = $id;
            """, [("$id", (object?)operationId)], row => (Omitted: row.GetInt32(0), Count: row.GetInt32(1), Unconfirmed: row.GetInt32(2), Cases: row.GetInt32(3))).FirstOrDefault();
        var omitted = coverage.Omitted;
        var completeSingleCase = coverage.Cases == 1 && coverage.Count > 0 && omitted == 0 && coverage.Unconfirmed == 0
            && statements.Select(row => (row.CaseId, row.ParameterSet)).Distinct().Count() == 1;
        var rows = statements.GroupBy(row => row.Id).Select(group =>
        {
            var row = group.First();
            var ambiguousNames = group.Where(item => item.ObjectName is not null)
                .GroupBy(item => item.ObjectName!, StringComparer.OrdinalIgnoreCase)
                .Where(items => items.Count() > 1).Select(items => items.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var objects = group.Where(item => item.ObjectName is not null).Select(item => new TableRow(
                item.ObjectName!, null, item.ObjectScans, null,
                item.ObjectReads is { } afterReads ? SaturatingAdd(afterReads, item.ObjectLobReads ?? 0) : null,
                string.Empty) { ObjectKind = item.ObjectKind, LobLogicalReadsAfter = completeSingleCase && item.EvidenceEligible && !ambiguousNames.Contains(item.ObjectName!) ? item.ObjectLobReads : null }).ToArray();
            var reads = objects.Length > 0 && objects.All(item => item.ReadsAfter.HasValue)
                ? objects.Aggregate(0L, (sum, item) => SaturatingAdd(sum, item.ReadsAfter!.Value)) : (long?)null;
            var scans = objects.Length > 0 && objects.All(item => item.ScansAfter.HasValue)
                ? objects.Aggregate(0L, (sum, item) => SaturatingAdd(sum, item.ScansAfter!.Value)) : (long?)null;
            var text = row.Text ?? $"statement {row.Ordinal + 1} ({row.Hash[..Math.Min(8, row.Hash.Length)]})";
            IReadOnlyList<HiddenWork> hidden;
            try { hidden = JsonSerializer.Deserialize<HiddenWork[]>(row.HiddenWorkJson, ConditionsJsonOptions) ?? []; }
            catch (JsonException) { hidden = []; }
            return new StatementRow(row.Key, text, "measured", null, reads, null, scans, row.Executions,
                string.Empty, row.Status, hidden, objects)
            {
                CpuAfterMs = completeSingleCase ? row.CpuMs : null,
                ElapsedAfterMs = completeSingleCase ? row.ElapsedMs : null,
                PlanRepetitionAfter = completeSingleCase ? row.EvidenceRepetition : null,
                Occurrence = row.Occurrence,
                PlanHashAfter = row.PlanHash,
                PlanLinkAfter = PlanLink(row),
                UdfElapsedMilliseconds = row.UdfElapsedMs,
            };
        }).ToArray();
        var attribution = coverage.Count > 0 && omitted == 0 && coverage.Unconfirmed == 0
            ? "confirmed" : coverage.Count == 0 && omitted == 0 ? "unavailable" : "partial";
        return new OperationStatementEvidenceResult(rows, omitted, attribution);
    }

    private static string? PlanLink(StoredStatement statement) =>
        statement.PlanRepetition is { } repetition && statement.PlanDocumentOrdinal is { } document
            && statement.PlanStatementIndex is { } index
            ? $"/operations/{statement.OperationId}?planCase=s:{Uri.EscapeDataString(statement.CaseId)}&planVariant={Uri.EscapeDataString(statement.Variant)}&{(statement.ParameterSet is null ? "planParameterSetNull=true" : $"planParameterSet={Uri.EscapeDataString(statement.ParameterSet)}")}&planRepetition={repetition.ToString(CultureInfo.InvariantCulture)}&planDocument={document.ToString(CultureInfo.InvariantCulture)}&planStatement={index.ToString(CultureInfo.InvariantCulture)}"
            : null;

    public SqlVersionText? SessionSqlText(long sessionId, string hash)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        var rows = Query(connection, """
            WITH matching_rows AS (
                SELECT id, 0 AS side, CASE WHEN sql_text IS NULL THEN 0 ELSE 1 END AS has_text
                FROM operations
                WHERE session_id = $session AND sql_hash = $hash
                UNION ALL
                SELECT id, 1 AS side, CASE WHEN candidate_sql_text IS NULL THEN 0 ELSE 1 END AS has_text
                FROM operations
                WHERE session_id = $session AND candidate_sql_hash = $hash
            )
            SELECT
                CASE WHEN length(CAST(CASE WHEN selected.side = 0 THEN operation.sql_text ELSE operation.candidate_sql_text END AS BLOB)) <= $maximum
                     THEN CASE WHEN selected.side = 0 THEN operation.sql_text ELSE operation.candidate_sql_text END END AS text,
                CASE WHEN selected.has_text = 1
                          AND length(CAST(CASE WHEN selected.side = 0 THEN operation.sql_text ELSE operation.candidate_sql_text END AS BLOB)) > $maximum
                     THEN 1 ELSE 0 END AS too_large
            FROM (
                SELECT id, side, has_text
                FROM matching_rows
                ORDER BY has_text DESC, id DESC, side ASC
                LIMIT 1
            ) AS selected
            JOIN operations AS operation ON operation.id = selected.id;
            """, [("$session", sessionId), ("$hash", hash), ("$maximum", MaximumSqlVersionBytes)],
            r => (Text: NullableString(r, 0), TooLarge: r.GetInt32(1) != 0));
        if (rows.Count == 0)
            return null;
        return new SqlVersionText(hash, rows[0].Text, rows[0].TooLarge);
    }

    public SqlVersionText? AgentSessionSqlText(AgentSessionKey key, string hash)
    {
        var sessionIds = AgentSessionMemberships().Where(item => item.Key == key && item.Membership == Membership.Exact)
            .Select(item => item.SessionId).Distinct().OrderByDescending(id => id);
        foreach (var sessionId in sessionIds)
        {
            var text = SessionSqlText(sessionId, hash);
            if (text is not null)
                return text;
        }
        return null;
    }

    /// <summary>Full retained compare history is versioned before applying the endpoint page.</summary>
    public Page<CompareAttemptSummary> CompareAttempts(long sessionId, long? cursor = null, int limit = CompareHistoryPageLimit)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new Page<CompareAttemptSummary>([], null);
        var history = CompareHistoryBuilder.Build(CompareHistoryRows(connection, sessionId));
        var pageLimit = Math.Clamp(limit, 1, MaximumLimit);
        var ordered = history.OrderByDescending(item => item.StartedAt, StringComparer.Ordinal)
            .ThenByDescending(item => item.OperationId).ToArray();
        var start = 0;
        if (cursor is not null)
        {
            var anchor = Array.FindIndex(ordered, item => item.OperationId == cursor.Value);
            if (anchor < 0)
                return new Page<CompareAttemptSummary>([], null);
            start = anchor + 1;
        }
        var page = ordered.Skip(start).Take(pageLimit).ToArray();
        page = HydrateCompareEvidence(connection, page).ToArray();
        return new Page<CompareAttemptSummary>(page, start + page.Length < ordered.Length ? page[^1].OperationId : null);
    }

    /// <summary>Reads bounded mutation history newest first; SQL and values are present only when sensitive journaling was enabled.</summary>
    public Page<MutationRequestSummary> MutationRequests(long? cursor = null, int limit = MutationHistoryPageLimit)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new Page<MutationRequestSummary>([], null);
        var pageLimit = Math.Clamp(limit, 1, MaximumLimit);
        var rows = Query(connection, """
            SELECT id, request_id, session_key, profile, engine, server, database_name, tier, mode, digest,
                   sql_text, parameters_json, state, status, verification, created_at, decided_at, finished_at, environment
            FROM mutation_requests
            WHERE $cursor IS NULL OR id < $cursor
            ORDER BY id DESC LIMIT $limit;
            """, [("$cursor", cursor), ("$limit", pageLimit + 1)], reader => new MutationRequestSummary(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
            NullableString(reader, 10), NullableString(reader, 11), reader.GetString(12), NullableString(reader, 13),
            NullableString(reader, 14), reader.GetString(15), NullableString(reader, 16), NullableString(reader, 17), NullableString(reader, 18)));
        var hasMore = rows.Count > pageLimit;
        var page = rows.Take(pageLimit).ToArray();
        return new Page<MutationRequestSummary>(page, hasMore ? page[^1].Id : null);
    }

    private IReadOnlyList<CompareHistoryRow> CompareHistoryRows(SqliteConnection connection, long sessionId)
    {
        var liveness = new ProcessLiveness(processes, _dead);
        return Query(connection, """
        SELECT id, session_id, operation, compare_subject, compare_scope, sql_hash, candidate_sql_hash, status, started_at,
               host_pid, host_started_at
        FROM operations
        WHERE session_id = $session AND (operation = 'compare' OR (operation = 'measure'
            AND compare_subject IS NOT NULL AND compare_scope IS NOT NULL))
        ORDER BY started_at, id;
        """, [("$session", sessionId)], reader => new CompareHistoryRow(
            reader.GetInt64(0), reader.GetInt64(1), NullableString(reader, 3), NullableString(reader, 4),
            NullableString(reader, 5), NullableString(reader, 6),
            liveness.Status(reader.GetString(7), reader.GetInt64(9), NullableString(reader, 10)), reader.GetString(8))
        { IsMeasure = reader.GetString(2) == "measure" });
    }

    private IReadOnlyList<CompareAttemptSummary> HydrateCompareEvidence(
        SqliteConnection connection, IReadOnlyList<CompareAttemptSummary> attempts)
    {
        var requested = attempts.DistinctBy(attempt => attempt.OperationId).ToArray();
        if (requested.Length == 0)
            return [];

        var evidence = new Dictionary<long, (JsonElement? Summary, JsonElement? Conditions)>();
        foreach (var batch in requested.Chunk(200))
        {
            var ids = batch.Select(attempt => attempt.OperationId).ToArray();
            CompareEvidenceQueryObserver?.Invoke(ids);
            var parameters = ids.Select((id, index) => ($"$id{index}", (object?)id)).ToArray();
            var placeholders = string.Join(",", parameters.Select(parameter => parameter.Item1));
            foreach (var row in Query(connection, $"""
                SELECT id, summary_json, conditions_json
                FROM operations WHERE operation = 'compare' AND id IN ({placeholders});
                """, parameters, reader => (
                    Id: reader.GetInt64(0),
                    Summary: Json(NullableString(reader, 1)),
                    Conditions: Json(NullableString(reader, 2)))))
                evidence[row.Id] = (row.Summary, row.Conditions);
        }

        return requested.Select(attempt => evidence.TryGetValue(attempt.OperationId, out var row)
            ? CompareHistoryBuilder.WithEvidence(attempt, row.Summary, row.Conditions)
            : attempt).ToArray();
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
                ("$unprofiled", query.UnprofiledOnly ? 1 : 0),
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
                  AND (($unprofiled = 1 AND o.profile IS NULL) OR ($unprofiled = 0 AND ($profile IS NULL OR o.profile = $profile)))
                  AND ($from IS NULL OR o.started_at >= $from)
                  AND ($to IS NULL OR o.started_at < $to)
                  AND ($cursor IS NULL OR o.id < $cursor)
                ORDER BY o.id DESC LIMIT $limit;
                """,
                parameters,
                reader => (Operation: ReadOperation(reader, liveness),
                    Variables: OperationDimensionResolver.ReadRecordedValues(NullableString(reader, 43))));
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
        var attemptsBySession = page.Where(item => item.Operation == "compare").Select(item => item.SessionId).Distinct()
            .ToDictionary(sessionId => sessionId, sessionId => CompareHistoryBuilder.Build(CompareHistoryRows(connection, sessionId)));
        page = page.Select(item => item.Operation == "compare" && attemptsBySession.TryGetValue(item.SessionId, out var attempts)
            ? item with
            {
                VersionPair = attempts.FirstOrDefault(attempt => attempt.OperationId == item.Id) is { BaselineVersion: { } a, CandidateVersion: { } b }
                    ? $"{a} → {b}"
                    : null,
            }
            : item).ToArray();
        var next = matches.Count > limit ? page[^1].Id : (long?)null;
        return new Page<OperationSummary>(page, next);
    }

    public Page<OperationSummary> NamedOperations(string? search = null, long? cursor = null, int limit = DefaultLimit)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new Page<OperationSummary>([], null);
        connection.CreateFunction<string?, string?>("sqlh_fold", FoldForSearch, isDeterministic: true);
        var folded = FoldForSearch(search?.Trim());
        var escaped = folded is null ? null : $"%{EscapeLike(folded)}%";
        var boundedLimit = Clamp(limit);
        var liveness = new ProcessLiveness(processes, _dead);
        var rows = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE ($cursor IS NULL OR o.id < $cursor)
              AND (COALESCE((SELECT agent_keep FROM operation_annotations a WHERE a.operation_id = o.id), 0) = 1
                   OR NULLIF((SELECT agent_label FROM operation_annotations a WHERE a.operation_id = o.id), '') IS NOT NULL
                   OR COALESCE((SELECT owner_starred FROM operation_annotations a WHERE a.operation_id = o.id), 0) = 1)
              AND ($search IS NULL OR
                   sqlh_fold(COALESCE((SELECT agent_label FROM operation_annotations a WHERE a.operation_id = o.id), '')) LIKE $search ESCAPE '\' OR
                   sqlh_fold(COALESCE((SELECT owner_note FROM operation_annotations a WHERE a.operation_id = o.id), '')) LIKE $search ESCAPE '\' OR
                   sqlh_fold(o.operation) LIKE $search ESCAPE '\' OR sqlh_fold(s.session_key) LIKE $search ESCAPE '\' OR
                   sqlh_fold(COALESCE(o.profile, '')) LIKE $search ESCAPE '\' OR sqlh_fold(COALESCE(o.database, '')) LIKE $search ESCAPE '\')
            ORDER BY o.id DESC LIMIT $limit;
            """, [("$cursor", cursor), ("$search", escaped), ("$limit", boundedLimit + 1)],
            reader => ReadOperation(reader, liveness));
        var page = rows.Take(boundedLimit).ToArray();
        return new Page<OperationSummary>(page, rows.Count > boundedLimit ? page[^1].Id : null);
    }

    public OperationDetail? Operation(
        long id,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? profileDimensions = null,
        bool storeSensitive = true)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        var schemaVersion = Scalar<long>(connection, "PRAGMA user_version;", []);
        var liveness = new ProcessLiveness(processes, _dead);
        var summary = Query(connection, $"""
            SELECT {OperationColumns} FROM operations o JOIN sessions s ON s.id = o.session_id WHERE o.id = $id;
            """, [("$id", id)], reader => ReadOperation(reader, liveness)).FirstOrDefault();
        if (summary is null)
            return null;

        var extra = Query(connection, """
            SELECT vars_json, candidate_sql_hash, sql_text, candidate_sql_text, raw_tokens, emitted_tokens, artifact_dir, summary_json,
                   client_call_id, agent_model, agent_reasoning_effort, agent_session_id, agent_turn_id, agent_turn_trigger, agent_thread_source,
                   raw_bytes, emitted_bytes, compare_subject, compare_scope, conditions_json, l.model, l.effort, c.title, setup_sql_text
            FROM operations
            LEFT JOIN operation_agent_links l ON l.operation_id = operations.id
            LEFT JOIN agent_conversations c ON c.id = l.conversation
            WHERE operations.id = $id;
            """, [("$id", id)], reader => (
                VarsJson: NullableString(reader, 0),
                CandidateHash: NullableString(reader, 1),
                SqlText: NullableString(reader, 2),
                CandidateText: NullableString(reader, 3),
                Raw: NullableLong(reader, 4),
                Emitted: NullableLong(reader, 5),
                Artifact: NullableString(reader, 6),
                Summary: Json(NullableString(reader, 7)),
                Agent: ReadAgent(reader),
                RawBytes: NullableLong(reader, 15),
                EmittedBytes: NullableLong(reader, 16),
                CompareSubject: NullableString(reader, 17), CompareScope: NullableString(reader, 18),
                CompareConditions: Json(NullableString(reader, 19)),
                SetupSqlText: NullableString(reader, 23))).Single();
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
        List<OperationPhaseSummary> phases = schemaVersion >= 10 ? Query(connection, """
            SELECT phase, count, total_ms, max_ms FROM operation_phases
            WHERE operation_id = $id ORDER BY phase;
            """, [("$id", id)], reader => new OperationPhaseSummary(
                reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3))) : [];
        List<OperationEventSummary> events = schemaVersion >= 10 ? Query(connection, """
            SELECT offset_ms, phase, duration_ms, outcome, error_number, detail_json
            FROM operation_events WHERE operation_id = $id ORDER BY seq LIMIT 257;
            """, [("$id", id)], ReadOperationEvent) : [];

        var compareVersion = summary.Operation == "compare"
            ? CompareHistoryBuilder.Build(CompareHistoryRows(connection, summary.SessionId))
                .FirstOrDefault(attempt => attempt.OperationId == id)
            : null;
        if (compareVersion is not null)
            compareVersion = CompareHistoryBuilder.WithEvidence(compareVersion, extra.Summary, extra.CompareConditions);
        var statementEvidence = OperationStatements(connection, id, storeSensitive);
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
            dimensions,
            extra.Agent,
            extra.RawBytes,
            extra.EmittedBytes,
            CompareSubject: extra.CompareSubject,
            CompareScope: extra.CompareScope,
            BaselineVersion: compareVersion?.BaselineVersion,
            CandidateVersion: compareVersion?.CandidateVersion,
            CompareRegression: compareVersion?.Regression,
            CompareCases: compareVersion?.Cases,
            CompareConditions: extra.CompareConditions,
            Phases: phases,
            Events: events.Count == 0 ? null : events,
            SetupSqlText: extra.SetupSqlText,
            Conditions: extra.CompareConditions,
            Statements: statementEvidence.Rows)
        {
            OmittedStatements = statementEvidence.OmittedStatements,
            StatementAttribution = statementEvidence.Attribution,
        };
    }

    public ReproScript? Reproduction(long operationId, string side, string caseId, ReproScriptForm form)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        var row = Query(connection, """
            SELECT engine, sql_text, candidate_sql_text, conditions_json FROM operations WHERE id = $id;
            """, [("$id", operationId)], reader => (
                Engine: NullableString(reader, 0),
                Sql: NullableString(reader, 1),
                CandidateSql: NullableString(reader, 2),
                Conditions: NullableString(reader, 3))).FirstOrDefault();
        if (row.Conditions is null)
            return null;

        ConditionsModel? conditions;
        try
        {
            conditions = JsonSerializer.Deserialize<ConditionsModel>(row.Conditions, ConditionsJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (conditions is null || !conditions.Cases.Any(item => string.Equals(item.Id, caseId, StringComparison.Ordinal)))
            return null;

        var sql = side switch
        {
            "baseline" => row.Sql,
            "candidate" => row.CandidateSql,
            "query" => row.Sql,
            _ => null,
        };
        return ReproScripts.Create(conditions with { Engine = row.Engine ?? conditions.Engine }, sql, side, caseId, form);
    }

    private static OperationEventSummary ReadOperationEvent(SqliteDataReader reader)
    {
        var detail = NullableString(reader, 5);
        string? clientConnectionId = null;
        int? droppedCount = null;
        if (detail is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(detail, new JsonDocumentOptions { MaxDepth = 4 });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("clientConnectionId", out var connectionId)
                        && connectionId.ValueKind == JsonValueKind.String)
                        clientConnectionId = connectionId.GetString();
                    if (document.RootElement.TryGetProperty("dropped", out var dropped)
                        && dropped.TryGetInt32(out var count) && count > 0)
                        droppedCount = count;
                }
            }
            catch (JsonException)
            {
                // Corrupt or future diagnostic detail must not break the operation page.
            }
        }

        return new OperationEventSummary(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4), clientConnectionId, droppedCount);
    }

    private static AgentCall? ReadAgent(SqliteDataReader r)
    {
        var agent = new AgentCall(NullableString(r, 8), NullableString(r, 9), NullableString(r, 10), NullableString(r, 11),
            NullableString(r, 12), NullableString(r, 13), NullableString(r, 14), NullableString(r, 20),
            NullableString(r, 21), NullableString(r, 22));
        return agent == new AgentCall(null, null, null, null, null, null, null) ? null : agent;
    }

    public OperationPlanManifest? Plans(long operationId, bool storeSensitive)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return null;

        var exists = Scalar<long>(connection, "SELECT COUNT(*) FROM operations WHERE id = $id;", [("$id", operationId)]) > 0;
        if (!exists)
            return null;
        if (!storeSensitive)
            return new OperationPlanManifest(operationId, "plans not stored", []);

        var occurrences = ReadPlanOccurrences(connection, "m.operation_id = $operation",
            [("$operation", operationId)], repetition: null);
        return new OperationPlanManifest(operationId, "stored", occurrences);
    }

    public PlanSourceMapSelection? PlanSourceMap(long operationId, string variant, string? parameterSet,
        int repetition, string? caseId, bool storeSensitive)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return null;

        var operation = Query(connection, "SELECT engine, sql_text, candidate_sql_text FROM operations WHERE id = $id;",
            [("$id", operationId)], reader => new
            {
                Engine = NullableString(reader, 0),
                SqlText = NullableString(reader, 1),
                CandidateSqlText = NullableString(reader, 2),
            }).FirstOrDefault();
        if (operation is null)
            return null;
        if (!storeSensitive)
            return new PlanSourceMapSelection(operationId, "plans not stored", operation.Engine, variant, parameterSet,
                caseId, null, repetition, null, []);

        var metrics = Query(connection, """
            SELECT id, variant, parameter_set, case_id, case_label
            FROM operation_metrics
            WHERE operation_id = $operation AND lower(variant) = lower($variant)
                AND parameter_set IS $parameterSet
                AND ($caseId IS NULL OR case_id = $caseId)
            ORDER BY ordinal;
            """, [("$operation", operationId), ("$variant", variant), ("$parameterSet", parameterSet), ("$caseId", caseId)], reader =>
            (Id: reader.GetInt64(0), Variant: reader.GetString(1), ParameterSet: NullableString(reader, 2),
                CaseId: reader.GetString(3), CaseLabel: reader.GetString(4)));

        if (metrics.Count == 0)
            return null;
        if (metrics.Count > 1)
            return new PlanSourceMapSelection(operationId, "stored", operation.Engine, variant, parameterSet,
                caseId, null, repetition, null, [], "case_required",
                "The selected variant and parameter set match multiple cases; supply caseId.");

        var metric = metrics[0];
        var executedText = string.Equals(metric.Variant, "candidate", StringComparison.OrdinalIgnoreCase)
            ? operation.CandidateSqlText
            : operation.SqlText;
        if (executedText is null)
            return new PlanSourceMapSelection(operationId, "stored", operation.Engine, metric.Variant, metric.ParameterSet,
                metric.CaseId, metric.CaseLabel, repetition, null, [], "executed_sql_unavailable",
                "The exact executed SQL text is unavailable in the sensitive journal.");

        var occurrences = ReadPlanOccurrences(connection, "m.id = $metric", [("$metric", metric.Id)], repetition);
        if (occurrences.Count == 0)
            return null;

        return new PlanSourceMapSelection(operationId, "stored", operation.Engine, metric.Variant, metric.ParameterSet,
            metric.CaseId, metric.CaseLabel, repetition, executedText, occurrences);
    }

    public StoredPlan? Plan(string hash)
    {
        using var connection = OpenReadOnly();
        if (connection is null)
            return null;
        return Query(connection, "SELECT sha256, format, raw_size, gz FROM plan_documents WHERE sha256 = $hash;", [("$hash", hash)], reader =>
        {
            var sha256 = reader.GetString(0);
            var format = reader.GetString(1);
            var rawSize = reader.GetInt64(2);
            if (rawSize < 0 || rawSize > MaximumStoredPlanBytes)
                throw new InvalidDataException("The stored plan document exceeds the raw byte limit.");

            using var gzip = new GZipStream(new MemoryStream((byte[])reader.GetValue(3)), CompressionMode.Decompress);
            using var output = new MemoryStream((int)rawSize);
            var buffer = new byte[8192];
            while (true)
            {
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (output.Length + read > rawSize || output.Length + read > MaximumStoredPlanBytes)
                    throw new InvalidDataException("The stored plan document does not match its declared size.");
                output.Write(buffer, 0, read);
            }

            if (output.Length != rawSize)
                throw new InvalidDataException("The stored plan document does not match its declared size.");
            var bytes = output.ToArray();
            var actualSha = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualSha, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The stored plan document does not match its content address.");

            string document;
            try
            {
                document = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The stored plan document is not valid UTF-8.", exception);
            }

            return new StoredPlan(sha256, format, document);
        }).FirstOrDefault();
    }

    private static IReadOnlyList<PlanOccurrenceManifest> ReadPlanOccurrences(SqliteConnection connection, string metricPredicate,
        (string Name, object? Value)[] parameters, int? repetition)
    {
        var repetitionClause = repetition is null ? string.Empty : " AND p.repetition = $repetition";
        (string Name, object? Value)[] queryParameters = repetition is null
            ? parameters
            : [.. parameters, ("$repetition", (object?)repetition.Value)];
        return Query(connection, $"""
            SELECT m.variant, m.parameter_set, m.case_id, m.case_label,
                p.repetition, p.document_ordinal, p.statement_index, p.span_start, p.span_length,
                p.statement_id, p.statement_comp_id, p.document_sha, p.omitted, p.match_kind,
                d.shape_hash, d.format, d.sha256
            FROM operation_metrics m
            JOIN plan_occurrences p ON p.metric_id = m.id
            LEFT JOIN plan_documents d ON d.sha256 = p.document_sha
            WHERE {metricPredicate}{repetitionClause}
            ORDER BY m.ordinal, p.repetition, p.document_ordinal, p.statement_index;
            """, queryParameters, reader =>
            {
                var omitted = reader.GetInt32(12) != 0;
                return new PlanOccurrenceManifest(
                    reader.GetString(0), NullableString(reader, 1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    NullableString(reader, 11), NullableString(reader, 14), NullableString(reader, 15),
                    reader.GetString(13), omitted, !omitted && !reader.IsDBNull(16));
            });
    }

    public DashboardStats Stats(
        StatsQuery query,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? profileDimensions = null)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return new DashboardStats([], [], [], [], [], [], [], [], [], 0, 0, [],
                new ProfileDimensionStats(query.Profile,
                    query.Profile is not null && profileDimensions?.ContainsKey(query.Profile) == true, 0, [], [], 0), []);
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
        (string, object?)[] dimensionWindow = [.. window, ("$profile", query.Profile), ("$unprofiled", query.UnprofiledOnly ? 1 : 0)];
        var dimensionRows = Query(connection, $"""
            SELECT o.profile, o.engine, o.server, o.database, o.status, o.duration_ms, o.vars_json
            FROM operations o WHERE {inWindow}
              AND (($unprofiled = 1 AND o.profile IS NULL) OR ($unprofiled = 0 AND $profile IS NOT NULL AND o.profile = $profile));
            """, dimensionWindow, r => new DimensionOperation(
                NullableString(r, 0), NullableString(r, 1), NullableString(r, 2), NullableString(r, 3),
                r.GetString(4), NullableLong(r, 5), NullableString(r, 6),
                OperationDimensionResolver.ReadRecordedValues(NullableString(r, 6))));
        var dimensionStats = AggregateProfileDimensions(query, dimensionRows, profileDimensions);
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
            (int)spills,
            (int)cold,
            profileOperations,
            dimensionStats,
            WorkflowCosts(connection, window));
    }

    private static IReadOnlyList<WorkflowCost> WorkflowCosts(SqliteConnection connection, (string, object?)[] window)
    {
        var rows = Query(connection, $"""
            SELECT o.operation_ref, o.parent_operation_ref, o.operation, o.status, o.emitted_bytes, o.dry_run,
                   root.raw_bytes, root.status, root.dry_run, root.operation
            FROM operations o
            LEFT JOIN operations root ON root.operation_ref = COALESCE(o.parent_operation_ref, o.operation_ref)
            WHERE ($from IS NULL OR o.started_at >= $from) AND ($to IS NULL OR o.started_at < $to);
            """, window, r => new WorkflowOutputRow(
                NullableString(r, 0), NullableString(r, 1), r.GetString(2), r.GetString(3), NullableLong(r, 4), r.GetInt64(5) != 0,
                NullableLong(r, 6), NullableString(r, 7), !r.IsDBNull(8) && r.GetInt64(8) != 0, NullableString(r, 9)));

        var costs = new List<WorkflowCost>();
        foreach (var group in rows.GroupBy(row => row.ParentOperationRef ?? row.OperationRef,
                     StringComparer.Ordinal))
        {
            var measuredRows = group.Where(row => row.EmittedBytes.HasValue).ToArray();
            if (measuredRows.Length == 0)
                continue;
            var referenced = group.Key is not null;
            var outputBytes = measuredRows.Sum(row => row.EmittedBytes!.Value);
            var estimatedTokens = measuredRows.Sum(row => OutputFootprint.EstimateTokens(row.EmittedBytes!.Value));
            var first = measuredRows[0];
            var comparable = referenced && group.All(row => row.Status == "succeeded")
                && first.RootRawBytes.HasValue && first.RootStatus == "succeeded"
                && !first.RootDryRun && first.RootOperation is not null && IsDatabaseExecution(first.RootOperation);
            var baselineBytes = comparable ? first.RootRawBytes!.Value : (long?)null;
            var comparison = comparable
                ? new WorkflowCostComparison("single-measured-execution-output", baselineBytes!.Value,
                    OutputFootprint.EstimateTokens(baselineBytes.Value), outputBytes - baselineBytes.Value,
                    OutputFootprint.EstimateTokens(baselineBytes.Value) - estimatedTokens)
                : null;
            var dryRuns = measuredRows.Count(row => row.DryRun);
            var executions = measuredRows.Count(row => !row.DryRun && IsDatabaseExecution(row.Operation));
            costs.Add(new WorkflowCost(group.Key, outputBytes, estimatedTokens, measuredRows.Length,
                OutputFootprint.EstimationMethod, referenced ? "workflow-output" : "legacy-artifact-volume",
                new WorkflowCostCoverage("unmeasured", "unmeasured", "unmeasured"), comparison, dryRuns, executions));
        }
        return costs.OrderBy(cost => cost.RootOperationRef, StringComparer.Ordinal).ToArray();
    }

    private static bool IsDatabaseExecution(string operation) => operation is
        "query" or "measure" or "compare" or "watch" or "snapshot" or "schema" or "ping" or "counts" or "space" or "qstop" or "indexes";

    private sealed record WorkflowOutputRow(string? OperationRef, string? ParentOperationRef, string Operation,
        string Status, long? EmittedBytes, bool DryRun, long? RootRawBytes, string? RootStatus, bool RootDryRun,
        string? RootOperation);

    private static ProfileDimensionStats AggregateProfileDimensions(
        StatsQuery query,
        IReadOnlyList<DimensionOperation> operations,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? profileDimensions)
    {
        var selectedProfileOperations = query.UnprofiledOnly
            ? operations.Where(operation => operation.Profile is null).ToArray()
            : query.Profile is null
                ? Array.Empty<DimensionOperation>()
                : operations.Where(operation => string.Equals(operation.Profile, query.Profile, StringComparison.Ordinal)).ToArray();
        var knownNames = query.Profile is not null && profileDimensions is not null
                         && profileDimensions.TryGetValue(query.Profile, out var configured)
            ? configured
            : Array.Empty<string>();
        var profileDefinitionAvailable = !query.UnprofiledOnly && query.Profile is not null && profileDimensions?.ContainsKey(query.Profile) == true;
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

        var allTargets = filtered.GroupBy(operation =>
                (operation.Profile, operation.Database, operation.Engine, operation.Server))
            .Select(group => new TargetStat(group.Key.Profile, group.Key.Database, group.Count(), group.Key.Engine, group.Key.Server))
            .OrderByDescending(target => target.Count)
            .ThenBy(target => target.Profile, StringComparer.Ordinal)
            .ThenBy(target => target.Engine, StringComparer.Ordinal)
            .ThenBy(target => target.Server, StringComparer.Ordinal)
            .ThenBy(target => target.Database, StringComparer.Ordinal)
            .ToArray();
        var targets = allTargets
            .Take(TopLimit)
            .ToArray();
        var matrix = BuildDimensionMatrix(query, dimensions, filtered);
        return new ProfileDimensionStats(query.Profile, profileDefinitionAvailable, filtered.Length, valueStats, targets, allTargets.Length, matrix);
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
                case_id, case_label,
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
                SELECT p.repetition, p.document_ordinal, COALESCE(p.document_sha, ''),
                    CASE WHEN p.omitted = 1 THEN 0 ELSE EXISTS (
                        SELECT 1 FROM plan_documents s WHERE s.sha256 = p.document_sha) END
                FROM plan_occurrences p WHERE p.metric_id = $id
                ORDER BY p.repetition, p.document_ordinal, p.statement_index;
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
        r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), 0, 0,
        NullableString(r, 14), Roots(NullableString(r, 15)), NullableString(r, 16),
        NullableString(r, 17), r.GetInt64(18) != 0, r.GetInt64(19) != 0, NullableString(r, 20), NullableString(r, 21))
    {
        HasCompare = r.GetInt64(22) != 0,
    };

    private IReadOnlyList<SessionSummary> WithOptimizationBadges(IReadOnlyList<SessionSummary> sessions) => sessions.Select(session =>
    {
        if (!session.HasCompare)
            return session;
        var evidence = OptimizationEvidence(session.Id);
        var section = OptimizationSummaryBuilder.Build(evidence);
        var badges = new List<SessionOptimizationBadge>();
        foreach (var subject in section.Subjects)
        {
            var added = false;
            var candidates = subject.Groups.SelectMany(group => group.Scopes.Select(scope => (Group: group, Scope: scope)))
                .OrderBy(item => item.Scope.Scope == "whole query" ? 0 : 1)
                .ThenBy(item => item.Scope.Scope, StringComparer.Ordinal);
            foreach (var (group, scope) in candidates)
            {
                if (scope.DefaultA is null)
                    continue;
                if (scope.DefaultB is null)
                {
                    badges.Add(new SessionOptimizationBadge(subject.Subject, "—", true));
                    added = true;
                    break;
                }
                var scopedGroup = group with { Scopes = [scope], DefaultA = scope.DefaultA, DefaultB = scope.DefaultB };
                var scopedEvidence = evidence.Where(item => item.Subject == subject.Subject
                    && (item.Database ?? string.Empty) == subject.Database
                    && item.Fingerprint == group.Fingerprint
                    && (item.Scope == scope.Scope || (scope.Scope == "whole query" && item.Scope == "whole"))).ToArray();
                var comparison = OptimizationComparer.Compare(scopedGroup, scopedEvidence,
                    scope.DefaultA, scope.DefaultB, "all", "sum");
                var before = comparison.VerdictBasis == "changed statements"
                    ? comparison.WholeBatch.ChangedBefore
                    : scope.Versions.FirstOrDefault(version => version.Version == scope.DefaultA)?.LogicalReadsSum;
                var after = comparison.VerdictBasis == "changed statements"
                    ? comparison.WholeBatch.ChangedAfter
                    : scope.Versions.FirstOrDefault(version => version.Version == scope.DefaultB)?.LogicalReadsSum;
                var change = before is null || after is null ? "—" : ChangeLabel(before.Value, after.Value);
                var warning = comparison.Mixed is not null
                    || !comparison.EquivalenceLabel.StartsWith("results equivalent", StringComparison.Ordinal);
                badges.Add(new SessionOptimizationBadge(subject.Subject, change, warning)
                {
                    VerdictBasis = comparison.VerdictBasis,
                });
                added = true;
                break;
            }
            if (!added)
                badges.Add(new SessionOptimizationBadge(subject.Subject, "—", true));
        }
        return session with { Optimization = badges };
    }).ToArray();

    private static string ChangeLabel(long before, long after)
    {
        if (before == 0)
            return after == 0 ? "0%" : $"+{after.ToString(CultureInfo.InvariantCulture)} pages";
        var percent = Math.Round((after - before) * 100d / before, MidpointRounding.AwayFromZero);
        return percent < 0
            ? $"−{Math.Abs(percent).ToString("0", CultureInfo.InvariantCulture)}%"
            : $"+{percent.ToString("0", CultureInfo.InvariantCulture)}%";
    }

    /// <summary>
    /// Search folding shared by the query and every searched column: NFC, then invariant
    /// lower case, so "łódź" finds "ŁÓDŹ". SQLite's own LIKE folds ASCII letters only.
    /// Accents still have to match.
    /// </summary>
    internal static string? FoldForSearch(string? value) =>
        value?.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static OperationSummary ReadOperation(SqliteDataReader r, ProcessLiveness liveness) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        liveness.Status(r.GetString(4), r.GetInt64(19), NullableString(r, 20)),
        r.IsDBNull(5) ? null : r.GetInt32(5), NullableString(r, 6), r.GetString(7), r.GetString(8), NullableString(r, 9),
        NullableLong(r, 10), NullableString(r, 11), NullableString(r, 12), NullableString(r, 13), NullableString(r, 14),
        r.GetInt64(15) != 0, NullableString(r, 16), NullableLong(r, 17), NullableLong(r, 21),
        r.GetInt64(22) != 0, r.GetInt64(23) != 0, r.GetInt64(24) != 0, Json(NullableString(r, 18)),
        NullableString(r, 25), NullableString(r, 26), NullableString(r, 27), NullableString(r, 28),
        NullableString(r, 29), NullableString(r, 30), r.GetInt64(31) != 0, NullableString(r, 32),
        NullableString(r, 33), null, NullableLong(r, 34), NullableLong(r, 35))
    {
        SafetyNotices = StringArray(NullableString(r, 36)),
        AgentLabelInitial = NullableString(r, 37),
        AgentLabel = NullableString(r, 38),
        AgentKeep = r.GetInt64(39) != 0,
        OwnerStarred = r.GetInt64(40) != 0,
        OwnerNote = NullableString(r, 41),
        DashboardEvidenceVersion = NullableInt(r, 42),
    };

    private static IReadOnlyList<string> StringArray(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<SessionRoot>? Roots(string? json)
    {
        if (json is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            return document.RootElement.EnumerateArray()
                .Select(item => new SessionRoot(
                    item.GetProperty("uri").GetString() ?? string.Empty,
                    item.TryGetProperty("name", out var name) ? name.GetString() : null))
                .ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static VariantDetail ReadVariant(SqliteDataReader r) => new(
        r.GetInt32(1), r.GetString(2), NullableString(r, 3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetInt32(5),
        SpreadAt(r, 6), SpreadAt(r, 9), SpreadAt(r, 12),
        NullableLong(r, 17), NullableLong(r, 18), NullableLong(r, 19), r.IsDBNull(20) ? null : r.GetInt32(20),
        NullableLong(r, 21), NullableLong(r, 22),
        r.GetInt32(23), r.GetInt64(24) != 0, r.GetInt64(25) != 0, r.GetInt32(26),
        Json(NullableString(r, 27)),
        r.IsDBNull(28) ? null : new PostgresBuffers(NullableLong(r, 28), NullableLong(r, 29), NullableLong(r, 30),
            NullableLong(r, 31), NullableLong(r, 32), NullableLong(r, 33)),
        [], [])
    {
        CaseId = r.GetString(15),
        CaseLabel = r.GetString(16),
    };

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

    private static ConditionsModel? DeserializeConditions(string? text)
    {
        if (text is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ConditionsModel>(text, ConditionsJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<CompareCaseEvidence> ReadOptimizationCases(
        JsonElement? summary, ConditionsModel? conditions, IReadOnlyList<CompareMetricEvidence> metrics)
    {
        var cases = new Dictionary<string, CompareCaseEvidence>(StringComparer.Ordinal);
        if (conditions?.Cases is { } conditionCases)
            foreach (var item in conditionCases)
                cases[item.Id] = new CompareCaseEvidence(item.Id, item.Label, item.SetName, null, null)
                { MeasurementFingerprint = item.EffectiveMeasurementFingerprint };

        var summaryCases = Property(summary, "cases");
        if (summaryCases is { ValueKind: JsonValueKind.Array } caseArray)
        {
            foreach (var item in caseArray.EnumerateArray())
            {
                var id = StringProperty(item, "caseId") ?? IntProperty(item, "index")?.ToString(CultureInfo.InvariantCulture) ?? "0";
                var current = cases.TryGetValue(id, out var found)
                    ? found
                    : new CompareCaseEvidence(id, StringProperty(item, "caseLabel") ?? $"Case {id}", null, null, null);
                var equivalence = Property(item, "equivalence");
                cases[id] = current with
                {
                    Label = StringProperty(item, "caseLabel") ?? current.Label,
                    ResultsEquivalent = BooleanProperty(equivalence, "equivalent"),
                    RegressionVerdict = StringProperty(Property(item, "regression"), "verdict"),
                };
            }
        }
        else if (cases.Count == 1)
        {
            var single = cases.Values.Single();
            cases[single.CaseId] = single with
            {
                ResultsEquivalent = BooleanProperty(summary, "resultsEquivalent"),
                RegressionVerdict = StringProperty(Property(summary, "regression"), "verdict"),
            };
        }

        foreach (var metric in metrics)
            if (!cases.ContainsKey(metric.CaseId))
                cases.Add(metric.CaseId, new CompareCaseEvidence(metric.CaseId, metric.CaseLabel, null, null, null)
                { MeasurementFingerprint = metric.MeasurementFingerprint });
        return cases.Values.OrderBy(item => conditions?.Cases?.FirstOrDefault(value => value.Id == item.CaseId)?.Index ?? int.MaxValue)
            .ThenBy(item => item.CaseId, StringComparer.Ordinal).ToArray();
    }

    private static bool? AggregateEquivalence(IReadOnlyList<CompareCaseEvidence> cases)
    {
        if (cases.Count == 0 || cases.Any(item => item.ResultsEquivalent is null))
            return null;
        return cases.All(item => item.ResultsEquivalent == true);
    }

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : null;

    private static string? StringProperty(JsonElement? element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    }

    private static bool? BooleanProperty(JsonElement? element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.True } ? true : value is { ValueKind: JsonValueKind.False } ? false : null;
    }

    private static int? IntProperty(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var result) ? result : null;
    }

    private static long? NullableLong(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetInt64(index);
    private static int? NullableInt(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetInt32(index);
    private static double? NullableDouble(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetDouble(index);
    private static long SaturatingAdd(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;

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