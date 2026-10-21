using System.ComponentModel;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Journal;

namespace SqlHarness.Mcp.Tools;

public sealed record AnnotationItemArgument(
    [property: Description("Opaque operationRef returned by an eligible journaled tool call.")] string OperationRef,
    [property: Description("Optional plain-text label; an empty string clears it.")] string? Label = null,
    [property: Description("Optional keep flag; omitted leaves the current value unchanged.")] bool? Keep = null);

public sealed record AnnotationItemResult(
    string OperationRef,
    string Status,
    string? Tool = null,
    string? Label = null,
    bool? Keep = null);

internal sealed record AnnotationReceiptSuccess(string OperationRef, string Status, string Tool, bool? Keep);
internal sealed record AnnotationUnavailable(string OperationRef, string Status);
internal sealed record AnnotationFullSuccess(string OperationRef, string Status, string Tool, string? Label, bool? Keep);

/// <summary>Resolves references and writes annotations using only the local journal.</summary>
internal static class AnnotateHandler
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal)
        { "query", "measure", "compare", "watch", "snapshot" };

    internal static bool LoadMatchingFlag(string configurationPath) =>
        SqlHarnessConfigLoader.Load(configurationPath).Config.Dashboard.AgentTranscripts.Enabled;

    internal static void Validate(IReadOnlyList<AnnotationItemArgument> items)
    {
        if (items is null || items.Count is < 1 or > 20)
            throw new McpMappingException("items must contain between 1 and 20 operations.");

        foreach (var item in items)
        {
            if (item is null || !Guid.TryParseExact(item.OperationRef, "N", out _))
                throw new McpMappingException("operationRef must be a 32-character hexadecimal reference.");
            McpOperationAnnotations.ValidateLabel(item.Label);
        }
    }

    internal static async Task<IReadOnlyList<AnnotationItemResult>> ApplyAsync(
        string databasePath,
        string sessionKey,
        string? currentAgentSessionId,
        IReadOnlyList<AnnotationItemArgument> items,
        bool matchingOn,
        CancellationToken cancellationToken,
        int responseBudgetBytes = McpLimits.MaxCallToolResultBudgetBytes,
        string responseDetail = "full")
    {
        var result = await ApplyCoreAsync(databasePath, sessionKey, currentAgentSessionId, items, matchingOn,
            cancellationToken, new McpResultBudget(responseBudgetBytes), responseDetail, null, wireResponse: false);
        return result.Cast<AnnotationItemResult>().ToArray();
    }

    internal static Task<IReadOnlyList<object>> ApplyForToolAsync(
        string databasePath,
        string sessionKey,
        string? currentAgentSessionId,
        IReadOnlyList<AnnotationItemArgument> items,
        bool matchingOn,
        CancellationToken cancellationToken,
        McpResultBudget responseBudget,
        string responseDetail,
        int processMaximumBytes) => ApplyCoreAsync(databasePath, sessionKey, currentAgentSessionId, items, matchingOn,
            cancellationToken, responseBudget, responseDetail, processMaximumBytes, wireResponse: true);

    private static async Task<IReadOnlyList<object>> ApplyCoreAsync(
        string databasePath,
        string sessionKey,
        string? currentAgentSessionId,
        IReadOnlyList<AnnotationItemArgument> items,
        bool matchingOn,
        CancellationToken cancellationToken,
        McpResultBudget responseBudget,
        string responseDetail,
        int? processMaximumBytes,
        bool wireResponse)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(items);
        if (responseDetail is not ("receipt" or "full"))
            throw new McpMappingException("responseDetail must be 'receipt' or 'full'.");

        var results = new List<AnnotationItemResult>(items.Count);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var caller = await ReadCallerAsync(connection, transaction, sessionKey, currentAgentSessionId, cancellationToken);
        if (caller is null)
        {
            foreach (var item in items)
                results.Add(Unavailable(item.OperationRef));
            var callerUnavailableResponse = ProjectResponse(results, responseDetail, wireResponse);
            EnsureFits(callerUnavailableResponse, responseBudget, processMaximumBytes ?? responseBudget.MaximumBytes, responseDetail);
            transaction.Commit();
            return wireResponse ? callerUnavailableResponse : results.Cast<object>().ToArray();
        }

        var targets = new Dictionary<string, TargetRow?>(StringComparer.Ordinal);
        var exactLinks = new List<ExactOperationLink>();
        foreach (var item in items)
        {
            if (targets.ContainsKey(item.OperationRef))
                continue;
            var target = await ReadTargetAsync(connection, transaction, item.OperationRef, cancellationToken);
            targets[item.OperationRef] = target;
            if (target is not null)
            {
                exactLinks.AddRange(await ReadLinksAsync(connection, transaction, target.OperationId, cancellationToken));
            }
        }

        if (caller.AgentKind == "claude")
            exactLinks.AddRange(await ReadSessionLinksAsync(connection, transaction, caller.SessionId, cancellationToken));

        var membership = new MembershipView(exactLinks.Distinct().ToArray());
        var timestamp = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[item.OperationRef];
            if (target is null || !AnnotationScope.Decide(caller, target, membership, matchingOn).Available)
            {
                results.Add(Unavailable(item.OperationRef));
                continue;
            }

            if (item.Label is not null || item.Keep is not null)
                await WriteAnnotationAsync(connection, transaction, target.OperationId, item, timestamp, cancellationToken);
            var annotation = await ReadAnnotationAsync(connection, transaction, target.OperationId, cancellationToken);
            results.Add(new AnnotationItemResult(item.OperationRef, "success", target.Tool, annotation.Label, annotation.Keep));
        }

        var response = ProjectResponse(results, responseDetail, wireResponse);
        EnsureFits(response, responseBudget, processMaximumBytes ?? responseBudget.MaximumBytes, responseDetail);

        transaction.Commit();
        return wireResponse ? response : results.Cast<object>().ToArray();
    }

    private static IReadOnlyList<object> ProjectResponse(
        IReadOnlyList<AnnotationItemResult> results, string responseDetail, bool wireResponse) =>
        results.Select(result => !wireResponse
            ? (object)result
            : result.Status != "success"
                ? new AnnotationUnavailable(result.OperationRef, result.Status)
                : responseDetail == "receipt"
                    ? new AnnotationReceiptSuccess(result.OperationRef, result.Status, result.Tool!, result.Keep)
                    : new AnnotationFullSuccess(result.OperationRef, result.Status, result.Tool!, result.Label, result.Keep))
            .ToArray();

    private static void EnsureFits(IReadOnlyList<object> response, McpResultBudget responseBudget,
        int processMaximumBytes, string responseDetail)
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, response, null);
        if (McpResultAdapter.FitsCompleteResult(outcome, "sqlharness_annotate", responseBudget))
            return;

        var nextAction = responseDetail == "full" && responseBudget.MaximumBytes < processMaximumBytes
            ? "use responseDetail 'receipt' or request a larger maxResultBytes within the process ceiling"
            : "use responseDetail 'receipt'";
        throw new McpMappingException($"The complete annotation response exceeds the effective {responseBudget.MaximumBytes}-byte result budget (maximum allowed {processMaximumBytes} bytes). {nextAction}. No annotations were changed.");
    }

    private static async Task<CallerIdentity?> ReadCallerAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionKey, string? agentSessionId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, agent_kind FROM sessions WHERE session_key = $key AND transport = 'mcp';";
        command.Parameters.AddWithValue("$key", sessionKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CallerIdentity(reader.GetInt64(0), reader.GetString(1), agentSessionId)
            : null;
    }

    private static async Task<TargetRow?> ReadTargetAsync(SqliteConnection connection, SqliteTransaction transaction,
        string operationRef, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.id, o.session_id, o.operation_ref, o.operation, s.agent_kind, o.agent_session_id,
                   o.operation_ref IS NOT NULL, o.dry_run
            FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE o.operation_ref = $ref;
            """;
        command.Parameters.AddWithValue("$ref", operationRef);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var tool = reader.GetString(3);
        return new TargetRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), tool,
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            Journaled: true, OperationRefEligible: reader.GetBoolean(6) && !reader.GetBoolean(7) && AllowedTools.Contains(tool));
    }

    private static async Task<IReadOnlyList<ExactOperationLink>> ReadLinksAsync(SqliteConnection connection,
        SqliteTransaction transaction, long operationId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT l.operation_id, o.session_id, c.agent_kind, c.conversation_id
            FROM operation_agent_links l JOIN operations o ON o.id = l.operation_id
            JOIN agent_conversations c ON c.id = l.conversation WHERE l.operation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", operationId);
        return await ReadLinksAsync(command, ct);
    }

    private static async Task<IReadOnlyList<ExactOperationLink>> ReadSessionLinksAsync(SqliteConnection connection,
        SqliteTransaction transaction, long sessionId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT l.operation_id, o.session_id, c.agent_kind, c.conversation_id
            FROM operation_agent_links l JOIN operations o ON o.id = l.operation_id
            JOIN agent_conversations c ON c.id = l.conversation WHERE o.session_id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        return await ReadLinksAsync(command, ct);
    }

    private static async Task<IReadOnlyList<ExactOperationLink>> ReadLinksAsync(SqliteCommand command, CancellationToken ct)
    {
        var links = new List<ExactOperationLink>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            links.Add(new ExactOperationLink(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        return links;
    }

    private static async Task WriteAnnotationAsync(SqliteConnection connection, SqliteTransaction transaction,
        long operationId, AnnotationItemArgument item, string timestamp, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operation_annotations(operation_id, agent_label_initial, agent_label, agent_keep, updated_at)
            VALUES ($id, NULL, $label, $keep, $now)
            ON CONFLICT(operation_id) DO UPDATE SET
                agent_label = CASE WHEN $hasLabel THEN $label ELSE operation_annotations.agent_label END,
                agent_keep = CASE WHEN $hasKeep THEN $keep ELSE operation_annotations.agent_keep END,
                updated_at = $now;
            """;
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$label", (object?)item.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$keep", (object?)(item.Keep is null ? null : item.Keep.Value ? 1 : 0) ?? DBNull.Value);
        command.Parameters.AddWithValue("$hasLabel", item.Label is null ? 0 : 1);
        command.Parameters.AddWithValue("$hasKeep", item.Keep is null ? 0 : 1);
        command.Parameters.AddWithValue("$now", timestamp);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string? Label, bool? Keep)> ReadAnnotationAsync(SqliteConnection connection,
        SqliteTransaction transaction, long operationId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT agent_label, agent_keep FROM operation_annotations WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1) != 0);
    }

    private static AnnotationItemResult Unavailable(string operationRef) =>
        new(operationRef, "operation_not_available");
}