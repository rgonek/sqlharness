using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using SqlHarness.Core.Journal;

using JournalOperationPhase = SqlHarness.Core.Journal.OperationPhase;

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
    private const int MaximumConditionsBytes = 1024 * 1024;
    internal const int MaximumPlanDocumentBytesPerOperation = 16 * 1024 * 1024;
    private const int MaximumPhaseEventDetailLength = 512;
    private const int MaximumPhaseEventInputLength = 4096;
    private const int MaximumPhaseEvents = OperationPhaseRecorder.MaximumEvents;

    private static readonly JsonSerializerOptions WaitJson = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly bool _storeSensitive;
    private readonly TextWriter _log;
    private readonly TimeProvider _time;
    private readonly int _maximumPlanDocumentBytesPerOperation;
    private int _warned;

    private ActivityJournal(string path, bool storeSensitive, TextWriter log, TimeProvider time, int maximumPlanDocumentBytesPerOperation)
    {
        _path = path;
        _storeSensitive = storeSensitive;
        _log = log;
        _time = time;
        _maximumPlanDocumentBytesPerOperation = maximumPlanDocumentBytesPerOperation;
    }

    public static IActivityJournal Open(JournalConfig config, TextWriter log) =>
        Open(SqlHarnessPaths.ActivityDatabase, config, log, TimeProvider.System);

    internal static IActivityJournal Open(string path, JournalConfig config, TextWriter log, TimeProvider time,
        int maximumPlanDocumentBytesPerOperation = MaximumPlanDocumentBytesPerOperation)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        if (!config.Enabled)
            return NullActivityJournal.Instance;

        var journal = new ActivityJournal(path, config.StoreSensitive, log, time, maximumPlanDocumentBytesPerOperation);
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
                    status, profile, vars_json, mutation_requested, sql_hash, candidate_sql_hash, sql_text, candidate_sql_text,
                    client_call_id, agent_model, agent_reasoning_effort, agent_session_id, agent_turn_id, agent_turn_trigger, agent_thread_source,
                    operation_ref, dry_run, compare_subject, compare_scope, compare_conditions_json, setup_sql_text)
                VALUES ($session, $operation, $hostPid, $hostStarted, $now, $now,
                    'running', $profile, $vars, $mutation, $sqlHash, $candidateHash, $sqlText, $candidateText,
                    $callId, $agentModel, $agentEffort, $agentSession, $agentTurn, $agentTrigger, $agentSource,
                    $operationRef, $dryRun, $compareSubject, $compareScope, $compareConditions, $setupSqlText)
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
            insert.Parameters.AddWithValue("$callId", (object?)start.Agent?.CallId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentModel", (object?)start.Agent?.Model ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentEffort", (object?)start.Agent?.ReasoningEffort ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentSession", (object?)start.Agent?.SessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentTurn", (object?)start.Agent?.TurnId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentTrigger", (object?)start.Agent?.TurnTrigger ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentSource", (object?)start.Agent?.ThreadSource ?? DBNull.Value);
            insert.Parameters.AddWithValue("$operationRef", (object?)start.OperationRef ?? DBNull.Value);
            insert.Parameters.AddWithValue("$dryRun", start.DryRun ? 1 : 0);
            insert.Parameters.AddWithValue("$compareSubject", (object?)start.Context?.Subject ?? DBNull.Value);
            insert.Parameters.AddWithValue("$compareScope", (object?)start.Context?.Scope ?? DBNull.Value);
            insert.Parameters.AddWithValue("$compareConditions", DBNull.Value);
            insert.Parameters.AddWithValue("$setupSqlText", _storeSensitive ? (object?)start.SetupSqlText ?? DBNull.Value : DBNull.Value);
            var id = (long)insert.ExecuteScalar()!;
            if (start.CreateAnnotation && !start.DryRun)
            {
                using var annotation = connection.CreateCommand();
                annotation.Transaction = transaction;
                annotation.CommandText = "INSERT INTO operation_annotations (operation_id, agent_label_initial, agent_label, agent_keep, updated_at) VALUES ($id, $label, $label, $keep, $now);";
                annotation.Parameters.AddWithValue("$id", id);
                annotation.Parameters.AddWithValue("$label", (object?)start.AgentLabel ?? DBNull.Value);
                annotation.Parameters.AddWithValue("$keep", start.AgentKeep is null ? DBNull.Value : start.AgentKeep.Value ? 1 : 0);
                annotation.Parameters.AddWithValue("$now", now);
                annotation.ExecuteNonQuery();
            }
            transaction.Commit();
            return new JournalHandle(id);
        }
        catch (Exception)
        {
            Warn();
            return null;
        }
    }

    /// <summary>Longest stored error message, including the trailing ellipsis of a truncated one.</summary>
    internal const int ErrorMessageLimit = 4096;

    internal static string? TruncateErrorMessage(string? message) =>
        message is null || message.Length <= ErrorMessageLimit ? message : message[..(ErrorMessageLimit - 1)] + "\u2026";

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
                UPDATE operations SET status = $status, exit_code = $exit, error_kind = $error, error_message = $message, duration_ms = $duration,
                    engine = $engine, server = $server, database = $database, result_sets = $sets, rows_returned = $rows,
                    raw_tokens = COALESCE($raw, raw_tokens), artifact_dir = $artifact, summary_json = $summary, cancel_reason = $cancel,
                    parent_operation_ref = $parentOperationRef, warnings_json = $warningsJson, finished_at = $now, updated_at = $now
                WHERE id = $id;
                UPDATE sessions SET last_seen = $now
                WHERE id = (SELECT session_id FROM operations WHERE id = $id);
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$status", end.Status);
            update.Parameters.AddWithValue("$exit", end.ExitCode);
            update.Parameters.AddWithValue("$error", (object?)end.ErrorKind ?? DBNull.Value);
            update.Parameters.AddWithValue("$message", (object?)TruncateErrorMessage(end.ErrorMessage) ?? DBNull.Value);
            update.Parameters.AddWithValue("$cancel", (object?)end.CancelReason ?? DBNull.Value);
            update.Parameters.AddWithValue("$duration", end.DurationMilliseconds);
            update.Parameters.AddWithValue("$engine", (object?)end.Engine ?? DBNull.Value);
            update.Parameters.AddWithValue("$server", (object?)end.Server ?? DBNull.Value);
            update.Parameters.AddWithValue("$database", (object?)end.Database ?? DBNull.Value);
            update.Parameters.AddWithValue("$sets", (object?)end.ResultSets ?? DBNull.Value);
            update.Parameters.AddWithValue("$rows", (object?)end.RowsReturned ?? DBNull.Value);
            update.Parameters.AddWithValue("$raw", (object?)end.RawTokens ?? DBNull.Value);
            update.Parameters.AddWithValue("$artifact", (object?)end.ArtifactDirectory ?? DBNull.Value);
            update.Parameters.AddWithValue("$summary", (object?)end.SummaryJson ?? DBNull.Value);
            update.Parameters.AddWithValue("$parentOperationRef", (object?)end.ParentOperationRef ?? DBNull.Value);
            update.Parameters.AddWithValue("$warningsJson", JsonSerializer.Serialize(end.Warnings ?? [], WaitJson));
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();

            using (var other = connection.CreateCommand())
            {
                other.Transaction = transaction;
                other.CommandText = """
                    INSERT INTO operation_phases (operation_id, phase, count, total_ms, max_ms)
                    SELECT $id, 'other', 1,
                        MAX(0, $duration - COALESCE(SUM(total_ms), 0)),
                        MAX(0, $duration - COALESCE(SUM(total_ms), 0))
                    FROM operation_phases WHERE operation_id = $id AND phase <> 'other'
                    ON CONFLICT(operation_id, phase) DO UPDATE SET
                        count = excluded.count, total_ms = excluded.total_ms, max_ms = excluded.max_ms;
                    """;
                other.Parameters.AddWithValue("$id", handle.OperationId);
                other.Parameters.AddWithValue("$duration", Math.Max(0, end.DurationMilliseconds));
                other.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        }
        catch (Exception)
        {
            Warn();
            return false;
        }
    }

    public void RecordPhases(
        JournalHandle? handle,
        IReadOnlyList<PhaseAggregate> phases,
        IReadOnlyList<PhaseEvent> events,
        int droppedEvents)
    {
        if (handle is null)
            return;
        try
        {
            using var connection = Connect();
            using var transaction = connection.BeginTransaction();
            foreach (var phase in phases)
            {
                using var insert = Command(connection, transaction, """
                    INSERT INTO operation_phases (operation_id, phase, count, total_ms, max_ms)
                    VALUES ($operation, $phase, $count, $total, $max)
                    ON CONFLICT(operation_id, phase) DO UPDATE SET
                        count = excluded.count, total_ms = excluded.total_ms, max_ms = excluded.max_ms;
                    """);
                insert.Parameters.AddWithValue("$operation", handle.OperationId);
                insert.Parameters.AddWithValue("$phase", phase.Phase.ToString().ToLowerInvariant());
                insert.Parameters.AddWithValue("$count", Math.Max(0, phase.Count));
                insert.Parameters.AddWithValue("$total", Math.Max(0, phase.TotalMilliseconds));
                insert.Parameters.AddWithValue("$max", Math.Max(0, phase.MaxMilliseconds));
                insert.ExecuteNonQuery();
            }

            var orderedEvents = events.OrderBy(phaseEvent => phaseEvent.Sequence).Take(MaximumPhaseEvents).ToArray();
            var eventCount = orderedEvents.Length;
            for (var index = 0; index < eventCount; index++)
                InsertPhaseEvent(connection, transaction, handle.OperationId, orderedEvents[index], index + 1);

            if (droppedEvents > 0)
            {
                using var insert = Command(connection, transaction, """
                    INSERT INTO operation_events (operation_id, seq, offset_ms, phase, duration_ms, outcome, error_number, detail_json)
                    VALUES ($operation, $seq, 0, 'other', 0, 'truncated', NULL, $detail);
                    """);
                insert.Parameters.AddWithValue("$operation", handle.OperationId);
                insert.Parameters.AddWithValue("$seq", eventCount + 1);
                insert.Parameters.AddWithValue("$detail", $$"""{"dropped":{{droppedEvents}}}""");
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception)
        {
            Warn();
        }
    }

    private static void InsertPhaseEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long operationId,
        PhaseEvent phaseEvent,
        int sequence)
    {
        using var insert = Command(connection, transaction, """
            INSERT INTO operation_events (operation_id, seq, offset_ms, phase, duration_ms, outcome, error_number, detail_json)
            VALUES ($operation, $seq, $offset, $phase, $duration, $outcome, $error, $detail);
            """);
        insert.Parameters.AddWithValue("$operation", operationId);
        insert.Parameters.AddWithValue("$seq", sequence);
        insert.Parameters.AddWithValue("$offset", Math.Max(0, phaseEvent.OffsetMilliseconds));
        insert.Parameters.AddWithValue("$phase", phaseEvent.Phase.ToString().ToLowerInvariant());
        insert.Parameters.AddWithValue("$duration", Math.Max(0, phaseEvent.DurationMilliseconds));
        insert.Parameters.AddWithValue("$outcome", phaseEvent.Outcome);
        insert.Parameters.AddWithValue("$error", (object?)phaseEvent.ErrorNumber ?? DBNull.Value);
        insert.Parameters.AddWithValue("$detail",
            (object?)AllowlistedPhaseDetail(phaseEvent.Phase, phaseEvent.DetailJson) ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private static string? AllowlistedPhaseDetail(JournalOperationPhase phase, string? detailJson)
    {
        if (string.IsNullOrEmpty(detailJson) || detailJson.Length > MaximumPhaseEventInputLength)
            return null;

        try
        {
            using var document = JsonDocument.Parse(detailJson, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                return null;

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var seenProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seenProperties.Add(property.Name))
                    return null;

                if (phase is JournalOperationPhase.Connect)
                {
                    switch (property.Name)
                    {
                        case "clientConnectionId":
                            if (property.Value.ValueKind is JsonValueKind.Null)
                                values.Add(property.Name, null);
                            else if (property.Value.ValueKind is JsonValueKind.String && property.Value.TryGetGuid(out var connectionId))
                                values.Add(property.Name, connectionId);
                            else
                                return null;
                            break;
                        case "authStrategy":
                            if (property.Value.ValueKind is not JsonValueKind.String || property.Value.GetString() is not (
                                "ad-default" or "azure-cli" or "sql" or "postgres"))
                                return null;
                            values.Add(property.Name, property.Value.GetString());
                            break;
                        case "attempt":
                            if (property.Value.ValueKind is not JsonValueKind.Number ||
                                !property.Value.TryGetInt32(out var attempt) || attempt <= 0)
                                return null;
                            values.Add(property.Name, attempt);
                            break;
                    }
                }
                else if (phase is JournalOperationPhase.Artifact && property.Name is "fileCount")
                {
                    if (property.Value.ValueKind is not JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out var fileCount) || fileCount < 0)
                        return null;
                    values.Add(property.Name, fileCount);
                }
            }

            if (values.Count == 0)
                return null;

            var serialized = JsonSerializer.Serialize(values);
            return serialized.Length <= MaximumPhaseEventDetailLength ? serialized : null;
        }
        catch (JsonException)
        {
            return null;
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
                UPDATE operations SET raw_tokens = $raw, emitted_tokens = $emitted,
                    raw_bytes = $rawBytes, raw_lines = $rawLines, emitted_bytes = $emittedBytes, emitted_lines = $emittedLines,
                    updated_at = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.Parameters.AddWithValue("$raw", raw is null ? DBNull.Value : raw.EstimatedTokenCount);
            update.Parameters.AddWithValue("$emitted", emitted.EstimatedTokenCount);
            update.Parameters.AddWithValue("$rawBytes", raw is null ? DBNull.Value : raw.Bytes);
            update.Parameters.AddWithValue("$rawLines", raw is null ? DBNull.Value : raw.Lines);
            update.Parameters.AddWithValue("$emittedBytes", emitted.Bytes);
            update.Parameters.AddWithValue("$emittedLines", emitted.Lines);
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

    public void RecordMutation(MutationJournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            var processInfo = ProcessInfo.Current;
            var hostPid = processInfo.CurrentPid;
            var hostStartedAt = processInfo.Get(hostPid)?.StartedAt;
            using var connection = Connect();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO mutation_requests (request_id, session_key, host_pid, host_started_at, profile, engine, server, database_name,
                    tier, mode, digest, environment, sql_text, parameters_json, state, created_at)
                VALUES ($requestId, $sessionKey, $hostPid, $hostStartedAt, $profile, $engine, $server, $database,
                    $tier, $mode, $digest, $environment, $sql, $parameters, 'pending', $createdAt);
                """;
            insert.Parameters.AddWithValue("$requestId", record.RequestId);
            insert.Parameters.AddWithValue("$sessionKey", record.SessionKey);
            insert.Parameters.AddWithValue("$hostPid", hostPid);
            insert.Parameters.AddWithValue("$hostStartedAt", Nullable(hostStartedAt));
            insert.Parameters.AddWithValue("$profile", record.Profile);
            insert.Parameters.AddWithValue("$engine", record.Engine);
            insert.Parameters.AddWithValue("$server", record.Server);
            insert.Parameters.AddWithValue("$database", record.Database);
            insert.Parameters.AddWithValue("$tier", record.Tier);
            insert.Parameters.AddWithValue("$mode", record.Mode);
            insert.Parameters.AddWithValue("$digest", record.Digest);
            insert.Parameters.AddWithValue("$environment", (object?)record.Environment ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sql", _storeSensitive ? (object?)record.Sql ?? DBNull.Value : DBNull.Value);
            insert.Parameters.AddWithValue("$parameters", _storeSensitive ? (object?)record.ParametersJson ?? DBNull.Value : DBNull.Value);
            insert.Parameters.AddWithValue("$createdAt", Timestamp(record.CreatedAt));
            insert.ExecuteNonQuery();
        }
        catch (Exception)
        {
            Warn();
        }
    }

    public void LinkMutation(string requestId, string? operationRef)
    {
        if (string.IsNullOrEmpty(operationRef)) return;
        try
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE mutation_requests SET operation_id =
                    (SELECT o.id FROM operations o JOIN sessions s ON s.id = o.session_id
                     WHERE o.operation_ref = $ref AND o.operation = 'mutation' AND s.dashboard_evidence_version = 1
                       AND (SELECT COUNT(*) FROM operations x WHERE x.operation_ref = $ref) = 1
                       AND NOT EXISTS (SELECT 1 FROM mutation_requests m WHERE m.operation_id = o.id))
                WHERE request_id = $requestId AND operation_id IS NULL;
                """;
            command.Parameters.AddWithValue("$requestId", requestId);
            command.Parameters.AddWithValue("$ref", operationRef);
            command.ExecuteNonQuery();
        }
        catch (Exception) { Warn(); }
    }

    public void RecordMutationResult(string requestId, int recordsAffected)
    {
        if (recordsAffected < 0) return;
        try
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE operations SET records_affected = $records
                WHERE id = (SELECT operation_id FROM mutation_requests WHERE request_id = $requestId)
                  AND EXISTS (SELECT 1 FROM sessions WHERE id = operations.session_id AND dashboard_evidence_version = 1);
                """;
            command.Parameters.AddWithValue("$requestId", requestId);
            command.Parameters.AddWithValue("$records", recordsAffected);
            command.ExecuteNonQuery();
        }
        catch (Exception) { Warn(); }
    }

    public void UpdateMutation(string requestId, string state, string? status, string? verification, DateTimeOffset at)
    {
        try
        {
            using var connection = Connect();
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE mutation_requests
                SET state = $state, status = COALESCE($status, status), verification = COALESCE($verification, verification),
                    decided_at = COALESCE(decided_at, CASE WHEN $state <> 'pending' THEN $at END),
                    finished_at = CASE WHEN $status IS NOT NULL THEN $at ELSE finished_at END
                WHERE request_id = $requestId AND ($state <> 'cancelled' OR state = 'pending');
                """;
            update.Parameters.AddWithValue("$requestId", requestId);
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
            update.Parameters.AddWithValue("$verification", (object?)verification ?? DBNull.Value);
            update.Parameters.AddWithValue("$at", Timestamp(at));
            update.ExecuteNonQuery();
        }
        catch (Exception)
        {
            Warn();
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
            var caseIdentities = ReadCaseIdentities(connection, transaction, handle.OperationId);
            var caseCount = Math.Max(1, record.Variants.Select(item => item.MatrixCell)
                .Concat(record.Statements.Select(item => item.MatrixCell)).OfType<int>().DefaultIfEmpty(0).Max() + 1);
            var admittedPlanDocuments = new Dictionary<string, JournalPlanDocument>(StringComparer.Ordinal);
            var admittedPlanShas = new HashSet<string>(StringComparer.Ordinal);
            var admittedPlanBytes = 0;
            for (var ordinal = 0; ordinal < record.Variants.Count; ordinal++)
            {
                var variant = record.Variants[ordinal];
                var identity = ResolveCaseIdentity(variant.MatrixCell, caseIdentities, caseCount, variant.CaseId, variant.CaseLabel);
                variant = variant with { CaseId = identity.Id, CaseLabel = identity.Label };
                var metricId = InsertMetric(connection, transaction, handle.OperationId, ordinal, variant);
                foreach (var table in variant.TableIo)
                    InsertTableIo(connection, transaction, metricId, table);
                if (_storeSensitive)
                {
                    foreach (var occurrence in variant.PlanOccurrences)
                    {
                        var document = occurrence.DocumentSha is null
                            ? null
                            : record.PlanDocuments.FirstOrDefault(item => item.Sha256 == occurrence.DocumentSha);
                        var omitted = document is null;
                        if (document is not null && !admittedPlanShas.Contains(document.Sha256))
                        {
                            var size = Encoding.UTF8.GetByteCount(document.Document);
                            if (size > _maximumPlanDocumentBytesPerOperation - admittedPlanBytes)
                                omitted = true;
                            else
                            {
                                admittedPlanBytes += size;
                                admittedPlanShas.Add(document.Sha256);
                                admittedPlanDocuments.TryAdd(document.Sha256, document);
                            }
                        }

                        using var insert = Command(connection, transaction, """
                            INSERT OR IGNORE INTO plan_occurrences
                                (metric_id, repetition, document_ordinal, statement_index, document_sha, omitted,
                                 span_start, span_length, match_kind, statement_id, statement_comp_id)
                            VALUES ($metric, $repetition, $documentOrdinal, $statementIndex, $sha, $omitted,
                                    $spanStart, $spanLength, $matchKind, $statementId, $statementCompId);
                            """);
                        insert.Parameters.AddWithValue("$metric", metricId);
                        insert.Parameters.AddWithValue("$repetition", occurrence.Repetition);
                        insert.Parameters.AddWithValue("$documentOrdinal", occurrence.DocumentOrdinal);
                        insert.Parameters.AddWithValue("$statementIndex", occurrence.StatementIndex);
                        insert.Parameters.AddWithValue("$sha", !omitted && document is not null ? document.Sha256 : DBNull.Value);
                        insert.Parameters.AddWithValue("$omitted", omitted ? 1 : 0);
                        insert.Parameters.AddWithValue("$spanStart", (object?)occurrence.SpanStart ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$spanLength", (object?)occurrence.SpanLength ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$matchKind", occurrence.MatchKind);
                        insert.Parameters.AddWithValue("$statementId", (object?)occurrence.StatementId ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$statementCompId", (object?)occurrence.StatementCompId ?? DBNull.Value);
                        insert.ExecuteNonQuery();
                    }
                }
            }

            for (var ordinal = 0; ordinal < record.Statements.Count; ordinal++)
            {
                var statement = record.Statements[ordinal];
                var identity = ResolveCaseIdentity(statement.MatrixCell, caseIdentities, caseCount, statement.CaseId, statement.CaseLabel);
                using var insert = Command(connection, transaction, """
                    INSERT INTO operation_statements (operation_id, ordinal, variant, parameter_set, matrix_cell,
                        case_id, case_label, statement_ordinal, statement_hash, statement_key, occurrence, statement_text,
                        executions, plan_hash, plan_repetition, plan_document_ordinal, plan_statement_index,
                        status, cpu_ms, elapsed_ms, dop, operators_json, udf_elapsed_ms, hidden_work_json)
                    VALUES ($operation, $ordinal, $variant, $set, $cell, $caseId, $caseLabel,
                        $statementOrdinal, $hash, $statementKey, $occurrence, $text,
                        $executions, $planHash, $planRepetition, $planDocumentOrdinal, $planStatementIndex,
                        $status, $cpu, $elapsed, $dop, $operators, $udfElapsed, $hiddenWork)
                    RETURNING id;
                    """);
                insert.Parameters.AddWithValue("$operation", handle.OperationId);
                insert.Parameters.AddWithValue("$ordinal", ordinal);
                insert.Parameters.AddWithValue("$variant", statement.Variant);
                insert.Parameters.AddWithValue("$set", (object?)statement.ParameterSet ?? DBNull.Value);
                insert.Parameters.AddWithValue("$cell", (object?)statement.MatrixCell ?? DBNull.Value);
                insert.Parameters.AddWithValue("$caseId", identity.Id);
                insert.Parameters.AddWithValue("$caseLabel", identity.Label);
                insert.Parameters.AddWithValue("$statementOrdinal", statement.StatementOrdinal);
                insert.Parameters.AddWithValue("$hash", statement.StatementHash);
                insert.Parameters.AddWithValue("$statementKey", statement.StatementKey);
                insert.Parameters.AddWithValue("$occurrence", statement.Occurrence);
                var fallbackText = $"statement {statement.StatementOrdinal + 1} ({statement.StatementHash[..Math.Min(8, statement.StatementHash.Length)]})";
                insert.Parameters.AddWithValue("$text", (object?)(_storeSensitive ? statement.Text : fallbackText) ?? DBNull.Value);
                insert.Parameters.AddWithValue("$executions", (object?)statement.Executions ?? DBNull.Value);
                insert.Parameters.AddWithValue("$planHash", (object?)statement.PlanHash ?? DBNull.Value);
                insert.Parameters.AddWithValue("$planRepetition", (object?)statement.PlanRepetition ?? DBNull.Value);
                insert.Parameters.AddWithValue("$planDocumentOrdinal", (object?)statement.PlanDocumentOrdinal ?? DBNull.Value);
                insert.Parameters.AddWithValue("$planStatementIndex", (object?)statement.PlanStatementIndex ?? DBNull.Value);
                insert.Parameters.AddWithValue("$status", statement.Status.ToString().ToLowerInvariant());
                insert.Parameters.AddWithValue("$cpu", (object?)statement.CpuMilliseconds ?? DBNull.Value);
                insert.Parameters.AddWithValue("$elapsed", (object?)statement.ElapsedMilliseconds ?? DBNull.Value);
                insert.Parameters.AddWithValue("$dop", (object?)statement.DegreeOfParallelism ?? DBNull.Value);
                insert.Parameters.AddWithValue("$operators", JsonSerializer.Serialize(statement.TopOperators, WaitJson));
                insert.Parameters.AddWithValue("$udfElapsed", (object?)statement.UdfElapsedMilliseconds ?? DBNull.Value);
                insert.Parameters.AddWithValue("$hiddenWork", JsonSerializer.Serialize(statement.HiddenWork, WaitJson));
                var statementId = (long)insert.ExecuteScalar()!;
                foreach (var counters in statement.Objects)
                {
                    using var objectInsert = Command(connection, transaction, """
                        INSERT INTO operation_statement_objects
                            (statement_id, object, kind, logical_reads, lob_logical_reads, scans)
                        VALUES ($statement, $object, $kind, $logical, $lob, $scans);
                        """);
                    objectInsert.Parameters.AddWithValue("$statement", statementId);
                    objectInsert.Parameters.AddWithValue("$object", counters.Object);
                    objectInsert.Parameters.AddWithValue("$kind", counters.Kind);
                    objectInsert.Parameters.AddWithValue("$logical", counters.LogicalReads);
                    objectInsert.Parameters.AddWithValue("$lob", counters.LobLogicalReads);
                    objectInsert.Parameters.AddWithValue("$scans", counters.Scans);
                    objectInsert.ExecuteNonQuery();
                }
            }

            if (_storeSensitive)
            {
                foreach (var document in admittedPlanDocuments.Values)
                {
                    var bytes = Encoding.UTF8.GetBytes(document.Document);
                    using var insert = Command(connection, transaction, """
                        INSERT OR IGNORE INTO plan_documents (sha256, shape_hash, format, raw_size, gz)
                        VALUES ($sha256, $shapeHash, $format, $size, $gz);
                        """);
                    insert.Parameters.AddWithValue("$sha256", document.Sha256);
                    insert.Parameters.AddWithValue("$shapeHash", document.ShapeHash);
                    insert.Parameters.AddWithValue("$format", document.Format);
                    insert.Parameters.AddWithValue("$size", bytes.LongLength);
                    insert.Parameters.AddWithValue("$gz", Gzip(bytes));
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

    private sealed record JournalCaseIdentity(string Id, string Label);

    private static Dictionary<int, JournalCaseIdentity> ReadCaseIdentities(
        SqliteConnection connection, SqliteTransaction transaction, long operationId)
    {
        try
        {
            using var query = Command(connection, transaction, "SELECT conditions_json FROM operations WHERE id = $id;");
            query.Parameters.AddWithValue("$id", operationId);
            if (query.ExecuteScalar() is not string json)
                return [];
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array)
                return [];
            var identities = new Dictionary<int, JournalCaseIdentity>();
            foreach (var item in cases.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var index) || !index.TryGetInt32(out var caseIndex)
                    || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String)
                    continue;
                identities[caseIndex] = new JournalCaseIdentity(id.GetString()!, label.GetString()!);
            }
            return identities;
        }
        catch (Exception)
        {
            // Missing, malformed, or irreducibly oversized conditions do not suppress benchmark rows.
            return [];
        }
    }

    private static JournalCaseIdentity ResolveCaseIdentity(
        int? matrixCell,
        IReadOnlyDictionary<int, JournalCaseIdentity> identities,
        int caseCount,
        string fallbackId,
        string fallbackLabel)
    {
        var index = matrixCell ?? 0;
        if (identities.TryGetValue(index, out var identity))
            return identity;
        return new JournalCaseIdentity(fallbackId,
            fallbackLabel == "Case 1 of 1" && caseCount > 1 ? $"Case {index + 1} of {caseCount}" : fallbackLabel);
    }

    public void RecordConditions(JournalHandle handle, ConditionsModel model)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            using var connection = Connect();
            var salt = ReadFingerprintSalt(connection);
            var sealedModel = SealConditions(model, salt);
            var persisted = BoundConditions(sealedModel, _storeSensitive);
            string? json = JsonSerializer.Serialize(persisted, WaitJson);
            if (Encoding.UTF8.GetByteCount(json!) > MaximumConditionsBytes)
            {
                persisted = OmitOversizedConditions(persisted);
                json = JsonSerializer.Serialize(persisted, WaitJson);
            }
            if (Encoding.UTF8.GetByteCount(json!) > MaximumConditionsBytes)
                json = null;

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE operations SET conditions_json = $json, conditions_fingerprint = $fingerprint, measurement_fingerprint = $measurementFingerprint WHERE id = $id;";
            update.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
            update.Parameters.AddWithValue("$fingerprint", persisted.Fingerprint);
            update.Parameters.AddWithValue("$measurementFingerprint", (object?)persisted.MeasurementFingerprint ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", handle.OperationId);
            update.ExecuteNonQuery();
        }
        catch (Exception)
        {
            Warn();
        }
    }

    private static byte[] ReadFingerprintSalt(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM journal_meta WHERE key = 'fingerprint_salt';";
        return (byte[]?)command.ExecuteScalar() ?? throw new InvalidOperationException("Journal fingerprint salt is unavailable.");
    }

    internal static ConditionsModel SealConditions(ConditionsModel model, byte[] salt)
    {
        ConditionsParameter SealParameter(ConditionsParameter parameter) =>
            parameter.ContentForFingerprint is { } content
                ? parameter with { ContentHash = Hmac(salt, content) }
                : parameter;

        var fixedParameters = model.Fixed.Select(SealParameter).ToArray();
        var fixedForFingerprint = model.FixedForFingerprint.Select(SealParameter).ToArray();
        var casesToSeal = model.Cases.Select(item => item with
        {
            Varying = item.Varying.Select(SealParameter).ToArray(),
            VaryingForFingerprint = item.VaryingForFingerprint.Select(SealParameter).ToArray(),
        }).ToArray();
        var fingerprintModel = model with
        {
            Fixed = fixedParameters,
            FixedForFingerprint = fixedForFingerprint,
            Cases = casesToSeal,
        };
        var setupHash = model.Setup is null ? string.Empty : Hmac(salt, ConditionsModelBuilder.SetupContentBytes(model));
        var cases = fingerprintModel.Cases.Select(item => item with
        {
            Fingerprint = Hmac(salt, ConditionsModelBuilder.CaseFingerprintPayload(fingerprintModel, setupHash, item)),
            MeasurementFingerprint = Hmac(salt, ConditionsModelBuilder.CaseMeasurementFingerprintPayload(fingerprintModel, setupHash, item)),
        }).ToArray();
        return fingerprintModel with
        {
            Setup = model.Setup is null ? null : model.Setup with { Hash = setupHash },
            Cases = cases,
            Fingerprint = Hmac(salt, ConditionsModelBuilder.OperationFingerprintPayload(fingerprintModel, setupHash)),
            MeasurementFingerprint = Hmac(salt, ConditionsModelBuilder.OperationMeasurementFingerprintPayload(fingerprintModel, setupHash)),
        };
    }

    private static string Hmac(byte[] salt, byte[] bytes) => Convert.ToHexStringLower(HMACSHA256.HashData(salt, bytes));

    internal static ConditionsModel BoundConditions(ConditionsModel model, bool storeSensitive)
    {
        var conditionsCase = model.Cases.Select(item => item with
        {
            Varying = item.Varying.Select(parameter => storeSensitive ? parameter : parameter with
            {
                Value = null,
                Rows = null,
                Values = null,
                SourceText = null,
            }).ToArray(),
        }).ToArray();
        conditionsCase = conditionsCase.Select(item => item with
        {
            Label = CaseLabel.Format(item with
            {
                Varying = model.Fixed.Where(parameter => parameter.Kind is "tvp" or "list").Concat(item.Varying).ToArray(),
            }, conditionsCase.Length, storeSensitive),
        }).ToArray();
        return model with
        {
            Setup = model.Setup is null ? null : model.Setup with { Text = storeSensitive ? model.Setup.Text : null },
            Fixed = model.Fixed.Select(parameter => storeSensitive ? parameter : parameter with
            {
                Value = null,
                Rows = null,
                Values = null,
                SourceText = null,
            }).ToArray(),
            Cases = conditionsCase,
        };
    }

    internal static ConditionsModel OmitOversizedConditions(ConditionsModel model)
    {
        var omitted = model.Omitted.ToList();
        if (model.Setup?.Text is not null)
        {
            model = model with { Setup = model.Setup with { Text = null } };
            omitted.Add("setup.text");
        }

        var candidates = model.Fixed.Select((parameter, index) => (IsFixed: true, Case: -1, Index: index, Parameter: parameter))
            .Concat(model.Cases.SelectMany((item, caseIndex) => item.Varying.Select((parameter, index) =>
                (IsFixed: false, Case: caseIndex, Index: index, Parameter: parameter))))
            .SelectMany(item => new[]
            {
                (item.IsFixed, item.Case, item.Index, item.Parameter, Field: "value", Size: Size(item.Parameter.Value)),
                (item.IsFixed, item.Case, item.Index, item.Parameter, Field: "rows", Size: Size(item.Parameter.Rows)),
                (item.IsFixed, item.Case, item.Index, item.Parameter, Field: "values", Size: Size(item.Parameter.Values)),
                (item.IsFixed, item.Case, item.Index, item.Parameter, Field: "sourceText", Size: item.Parameter.SourceText is null ? 0 : Encoding.UTF8.GetByteCount(item.Parameter.SourceText)),
            })
            .Where(item => item.Size > 0)
            .OrderByDescending(item => item.Size)
            .ToArray();
        foreach (var candidate in candidates)
        {
            var path = candidate.IsFixed
                ? $"fixed.{candidate.Parameter.Name}.{candidate.Field}"
                : $"cases.{model.Cases[candidate.Case].Id}.{candidate.Parameter.Name}.{candidate.Field}";
            ConditionsParameter Remove(ConditionsParameter parameter) => candidate.Field switch
            {
                "value" => parameter with { Value = null },
                "rows" => parameter with { Rows = null },
                "values" => parameter with { Values = null },
                "sourceText" => parameter with { SourceText = null },
                _ => parameter,
            };
            if (candidate.IsFixed)
            {
                var fixedParameters = model.Fixed.ToArray();
                fixedParameters[candidate.Index] = Remove(fixedParameters[candidate.Index]);
                model = model with { Fixed = fixedParameters };
            }
            else
            {
                var cases = model.Cases.ToArray();
                var varying = cases[candidate.Case].Varying.ToArray();
                varying[candidate.Index] = Remove(varying[candidate.Index]);
                cases[candidate.Case] = cases[candidate.Case] with { Varying = varying };
                model = model with { Cases = cases };
            }
            var relabeledCases = model.Cases.Select(conditionsCase => conditionsCase with
            {
                Label = CaseLabel.Format(conditionsCase with
                {
                    Varying = model.Fixed.Where(parameter => parameter.Kind is "tvp" or "list")
                        .Concat(conditionsCase.Varying).ToArray(),
                }, model.Cases.Count, valuesStored: true),
            }).ToArray();
            model = model with { Cases = relabeledCases };
            omitted.Add(path);
            model = model with { Omitted = omitted.ToArray() };
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(model, WaitJson)) <= MaximumConditionsBytes)
                break;
        }

        return model with { Omitted = omitted.ToArray() };

        static int Size(JsonElement? value) => value is { } item && item.ValueKind != JsonValueKind.Undefined
            ? JsonSerializer.SerializeToUtf8Bytes(item, WaitJson).Length
            : 0;
    }

    private void Initialize()
    {
        var directory = Path.GetDirectoryName(_path)!;
        SqlHarnessPaths.EnsureHomeFor(directory);
        Directory.CreateDirectory(directory);
        OwnerOnlyFiles.Directory(directory);
        try
        {
            Migrate();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            QuarantineIfStillCorrupt();
            Migrate();
        }
    }

    private void Migrate()
    {
        using var migrationLock = AcquireMigrationLock();
        long version;
        using (var connection = Connect())
        {
            version = UserVersion(connection);
            if (version > JournalSchema.CurrentVersion)
                throw new JournalSchemaTooNewException();
            if (version == JournalSchema.CurrentVersion)
                return;

            if (version > 0 && version < JournalSchema.FirstRetainedVersion)
            {
                if (HasPendingMutationRequests(connection))
                    throw new InvalidOperationException("The older journal contains pending mutation requests.");

                var checkpoint = Checkpoint(connection);
                if (checkpoint is { Busy: not 0 })
                    throw new InvalidOperationException("The older journal is in use by another process.");
            }
            else if (version != 0)
            {
                MigrateRetainedJournal(connection, version);
                return;
            }
        }

        if (version > 0)
        {
            var currentVersion = ReadVersionExclusively();
            if (currentVersion > JournalSchema.CurrentVersion)
                throw new JournalSchemaTooNewException();
            if (currentVersion == JournalSchema.CurrentVersion)
                return;
            if (currentVersion > 0 && currentVersion < JournalSchema.FirstRetainedVersion)
                RenameJournal($".v{currentVersion}-{_time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}.bak");
            else
                throw new InvalidOperationException("The journal version changed while the migration lock was held.");
        }

        using var fresh = Connect();
        CreateCurrentJournal(fresh);
    }

    internal void QuarantineIfStillCorrupt()
    {
        var suffix = ".corrupt-" + _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        using var migrationLock = AcquireMigrationLock();
        if (IsCorruptJournalAtCurrentPath())
            RenameJournal(suffix);
    }

    private bool IsCorruptJournalAtCurrentPath()
    {
        if (!File.Exists(_path))
            return false;

        try
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";
            return !string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            return true;
        }
    }

    private FileStream AcquireMigrationLock()
    {
        var lockPath = _path + ".upgrade.lock";
        var waitTimer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var migrationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                OwnerOnlyFiles.File(lockPath);
                return migrationLock;
            }
            catch (IOException) when (waitTimer.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(25);
            }
        }
    }

    private uint ReadVersionExclusively()
    {
        using var database = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        Span<byte> version = stackalloc byte[sizeof(uint)];
        database.Position = 60;
        database.ReadExactly(version);
        return BinaryPrimitives.ReadUInt32BigEndian(version);
    }

    private void RenameJournal(string suffix)
    {
        var moved = new List<(string Source, string Destination)>();
        try
        {
            foreach (var side in new[] { string.Empty, "-wal", "-shm" })
            {
                var source = _path + side;
                if (!File.Exists(source))
                    continue;
                var destination = _path + suffix + side;
                File.Move(source, destination);
                moved.Add((source, destination));
            }
        }
        catch
        {
            foreach (var (source, destination) in moved.AsEnumerable().Reverse())
            {
                if (File.Exists(destination) && !File.Exists(source))
                    File.Move(destination, source);
            }
            throw;
        }
    }

    private static bool HasPendingMutationRequests(SqliteConnection connection)
    {
        if (!TableExists(connection, "mutation_requests"))
            return false;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM mutation_requests WHERE state IN ('pending', 'approved', 'executing'));";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static (long Busy, long Log, long Checkpointed)? Checkpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        try
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            return null;
        }
    }

    private void CreateCurrentJournal(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL;");
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            Execute(connection, JournalSchema.Current);
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO journal_meta (key, value) VALUES ('fingerprint_salt', $salt);";
                insert.Parameters.AddWithValue("$salt", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                insert.ExecuteNonQuery();
            }
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

    private static void MigrateRetainedJournal(SqliteConnection connection, long version)
    {
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            var locked = UserVersion(connection);
            if (locked > JournalSchema.CurrentVersion)
                throw new JournalSchemaTooNewException();
            if (locked == JournalSchema.CurrentVersion)
            {
                Execute(connection, "ROLLBACK;");
                return;
            }
            if (locked != version || locked < JournalSchema.FirstRetainedVersion)
                throw new InvalidOperationException("The journal version changed while the migration lock was held.");
            for (var next = checked((int)locked + 1); next <= JournalSchema.CurrentVersion; next++)
            {
                if (!JournalSchema.Migrations.TryGetValue(next, out var migration))
                    throw new InvalidOperationException($"No journal migration is registered for version {next}.");
                Execute(connection, migration);
                Execute(connection, $"PRAGMA user_version = {next};");
            }
            Execute(connection, "COMMIT;");
        }
        catch
        {
            Execute(connection, "ROLLBACK;");
            throw;
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
            INSERT INTO sessions (session_key, agent_kind, transport, source, client_name, client_version, client_title, mcp_mode,
                agent_pid, agent_started_at, host_pid, host_started_at, cwd, roots_json, protocol_version, first_seen, last_seen, dashboard_evidence_version)
            VALUES ($key, $kind, $transport, $source, $clientName, $clientVersion, $clientTitle, $mcpMode,
                $agentPid, $agentStarted, $hostPid, $hostStarted, $cwd, $roots, $protocol, $now, $now, 1)
            ON CONFLICT(session_key) DO UPDATE SET
                last_seen = excluded.last_seen,
                client_name = COALESCE(sessions.client_name, excluded.client_name),
                client_version = COALESCE(sessions.client_version, excluded.client_version),
                client_title = COALESCE(sessions.client_title, excluded.client_title),
                roots_json = COALESCE(excluded.roots_json, sessions.roots_json),
                protocol_version = COALESCE(sessions.protocol_version, excluded.protocol_version)
            RETURNING id;
            """;
        upsert.Parameters.AddWithValue("$key", session.SessionKey);
        upsert.Parameters.AddWithValue("$kind", session.AgentKind);
        upsert.Parameters.AddWithValue("$transport", session.Transport == JournalTransport.Mcp ? "mcp" : "cli");
        upsert.Parameters.AddWithValue("$source", session.Source);
        upsert.Parameters.AddWithValue("$clientName", (object?)session.ClientName ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$clientVersion", (object?)session.ClientVersion ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$clientTitle", (object?)session.ClientTitle ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$mcpMode", (object?)session.McpMode ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$agentPid", (object?)session.AgentPid ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$agentStarted", Nullable(session.AgentStartedAt));
        upsert.Parameters.AddWithValue("$hostPid", session.HostPid);
        upsert.Parameters.AddWithValue("$hostStarted", Nullable(session.HostStartedAt));
        upsert.Parameters.AddWithValue("$cwd", (object?)session.Cwd ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$roots", (object?)session.RootsJson ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$protocol", (object?)session.ProtocolVersion ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$now", now);
        return (long)upsert.ExecuteScalar()!;
    }

    private static long InsertMetric(SqliteConnection connection, SqliteTransaction transaction, long operationId, int ordinal, JournalVariantMetrics v)
    {
        using var insert = Command(connection, transaction, """
            INSERT INTO operation_metrics (operation_id, ordinal, variant, parameter_set, matrix_cell, case_id, case_label, runs,
                elapsed_ms_min, elapsed_ms_median, elapsed_ms_max, cpu_ms_min, cpu_ms_median, cpu_ms_max,
                logical_reads_min, logical_reads_median, logical_reads_max,
                grant_requested_kb, grant_granted_kb, grant_max_used_kb, dop, compile_time_ms, compile_cpu_ms,
                spill_count, has_warnings, has_implicit_conversion, missing_index_count, waits_json,
                pg_shared_hit, pg_shared_read, pg_shared_dirtied, pg_shared_written, pg_temp_read, pg_temp_written,
                omitted_statements)
            VALUES ($operation, $ordinal, $variant, $set, $cell, $caseId, $caseLabel, $runs,
                $eMin, $eMed, $eMax, $cMin, $cMed, $cMax, $rMin, $rMed, $rMax,
                $gReq, $gGrant, $gUsed, $dop, $compileTime, $compileCpu,
                $spills, $warnings, $implicit, $missing, $waits,
                $pgHit, $pgRead, $pgDirtied, $pgWritten, $pgTempRead, $pgTempWritten,
                $omittedStatements)
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("$operation", operationId);
        insert.Parameters.AddWithValue("$ordinal", ordinal);
        insert.Parameters.AddWithValue("$variant", v.Variant);
        insert.Parameters.AddWithValue("$set", (object?)v.ParameterSet ?? DBNull.Value);
        insert.Parameters.AddWithValue("$cell", (object?)v.MatrixCell ?? DBNull.Value);
        insert.Parameters.AddWithValue("$caseId", v.CaseId);
        insert.Parameters.AddWithValue("$caseLabel", v.CaseLabel);
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
        insert.Parameters.AddWithValue("$omittedStatements", v.OmittedStatements);
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