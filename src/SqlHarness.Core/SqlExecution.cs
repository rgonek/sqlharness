using System.Data;
using System.Data.Common;
using System.Globalization;

using Microsoft.Data.SqlClient;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal sealed record SqlExecutionCommand(
    string Sql,
    IReadOnlyList<SqlHarnessParameter> Parameters,
    int TimeoutSeconds)
{
    // PostgreSQL fails closed when a new call site omits execution intent.
    internal PostgresExecutionMode PostgresMode { get; init; } = PostgresExecutionMode.ReadOnly;
}

internal enum PostgresExecutionMode
{
    ReadOnly,
    SessionSetup,
    ApprovedMutation,
}

internal interface ISqlReader : IAsyncDisposable
{
    int FieldCount { get; }
    int RecordsAffected { get; }
    string GetName(int ordinal);
    Type GetFieldType(int ordinal);
    string GetSqlTypeName(int ordinal) => GetFieldType(ordinal).Name;
    SqlReaderColumnMetadata GetColumnMetadata(int ordinal) => new(GetSqlTypeName(ordinal), null, null, null);
    bool GetAllowNull(int ordinal);

    /// <summary>
    /// The nullability the provider reported, or null when it reported none. Only the
    /// comparison path reads this (plan 046): every other surface keeps the coerced
    /// <see cref="GetAllowNull"/> value. Readers that always know their nullability
    /// inherit the coerced value.
    /// </summary>
    bool? GetReportedAllowNull(int ordinal) => GetAllowNull(ordinal);
    object GetValue(int ordinal);
    Task<bool> ReadAsync(CancellationToken ct);
    Task<bool> NextResultAsync(CancellationToken ct);
}

internal sealed record SqlReaderColumnMetadata(string TypeName, long? Size, byte? Precision, byte? Scale);

internal interface ISqlSession : IAsyncDisposable
{
    IReadOnlyList<SessionMessage> Messages { get; }
    SqlHarnessTargetIdentityReport Identity { get; set; }
    string? ClientConnectionId => null;
    Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct);

    /// <summary>Ends one read-only logical batch and returns any safety warnings it produced.</summary>
    Task<IReadOnlyList<string>> EndLogicalBatchAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Per-command message consumption: returns the messages queued at or after
    /// <paramref name="startIndex"/> so a session never retains notices from all
    /// previous repetitions. Production sessions additionally release the consumed
    /// buffer memory; the default keeps the legacy slice without releasing.
    /// </summary>
    ConsumedSessionMessages ConsumeMessages(int startIndex)
    {
        var messages = Messages.Skip(Math.Max(0, startIndex)).ToArray();
        var tableIo = new TableIoAccumulator();
        foreach (var message in messages)
            tableIo.Add(message.Number, message.Text);
        return new ConsumedSessionMessages(messages, 0, tableIo.Snapshot(), tableIo.Complete);
    }
}

internal sealed record SessionMessage(int Number, string Text);

internal static class SqlSessionLogicalBatch
{
    internal static async Task<IReadOnlyList<string>> EndAsync(ISqlSession session, CancellationToken ct)
    {
        var warnings = await session.EndLogicalBatchAsync(ct);
        _ = session.ConsumeMessages(0);
        return warnings;
    }
}

/// <summary>
/// One command's share of session messages. Diagnostics beyond the per-command
/// memory bound are truncated; <see cref="OmittedMessageCount"/> reports how many
/// arrivals were dropped since the previous consumption.
/// </summary>
internal sealed record ConsumedSessionMessages(
    IReadOnlyList<SessionMessage> Messages,
    int OmittedMessageCount,
    IReadOnlyList<TableIoCounters> TableIo,
    bool TableIoComplete,
    IReadOnlyDictionary<string, long>? RawTableLogicalReads = null)
{
    internal ConsumedSessionMessages(IReadOnlyList<SessionMessage> messages, int omittedMessageCount)
        : this(messages, omittedMessageCount, [], true) { }
}

/// <summary>
/// Bounded server-message store for one session (InfoMessage / NOTICE).
/// Holds at most <see cref="MaxMessagesPerCommand"/> messages and
/// <see cref="MaxCharactersPerCommand"/> characters as a sliding window: the
/// newest arrivals (including a command's trailing STATISTICS lines) always win
/// and older arrivals are evicted with a counter. Consumers additionally drain
/// the buffer once per command, so repetitions reuse the same bounded memory
/// instead of accumulating the whole session.
/// </summary>
internal sealed class SessionMessageBuffer
{
    /// <summary>Retained server messages per command window (diagnostics cap).</summary>
    internal const int MaxMessagesPerCommand = 1000;

    /// <summary>Retained server-message characters per command window (256 KiB).</summary>
    internal const int MaxCharactersPerCommand = 256 * 1024;

    private readonly object _sync = new();
    private readonly List<SessionMessage> _messages = new();
    private readonly TableIoAccumulator _tableIo = new();
    private long _retainedChars;
    private int _droppedSinceConsume;

    public int Count
    {
        get
        {
            lock (_sync)
                return _messages.Count;
        }
    }

    public void Add(int number, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            _tableIo.Add(number, message);
            _messages.Add(new SessionMessage(number, message));
            _retainedChars += message.Length;
            while ((_messages.Count > MaxMessagesPerCommand ||
                _retainedChars > MaxCharactersPerCommand) &&
                _messages.Count > 0)
            {
                // Evict oldest first. A single arrival larger than the whole
                // budget is dropped outright, so the bound always holds.
                SessionMessage oldest = _messages[0];
                _messages.RemoveAt(0);
                _retainedChars -= oldest.Text.Length;
                _droppedSinceConsume++;
            }
        }
    }

    public void Add(string message) => Add(0, message);

    public IReadOnlyList<SessionMessage> Snapshot()
    {
        lock (_sync)
            return _messages.ToArray();
    }

    /// <summary>
    /// Returns messages at or after <paramref name="startIndex"/> and releases all
    /// retained memory. The omitted count covers drops since the previous
    /// consumption, which is the current command's window when every command is
    /// consumed exactly once.
    /// </summary>
    public ConsumedSessionMessages Consume(int startIndex)
    {
        lock (_sync)
        {
            int start = Math.Clamp(startIndex, 0, _messages.Count);
            IReadOnlyList<SessionMessage> slice = _messages.Skip(start).ToArray();
            int omitted = _droppedSinceConsume;
            IReadOnlyList<TableIoCounters> tableIo = _tableIo.Snapshot();
            bool tableIoComplete = _tableIo.Complete;
            IReadOnlyDictionary<string, long> rawTableLogicalReads = _tableIo.RawLogicalReadsByTable;
            _messages.Clear();
            _retainedChars = 0;
            _droppedSinceConsume = 0;
            _tableIo.Reset();
            return new ConsumedSessionMessages(slice, omitted, tableIo, tableIoComplete, rawTableLogicalReads);
        }
    }
}

internal interface ISqlSessionFactory
{
    Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct);

    Task<ISqlSession> ConnectAsync(
        ResolvedTarget target,
        CancellationToken ct,
        List<string> knownSecrets) => ConnectAsync(target, ct);
}

internal sealed class SqlTargetMismatchException(string message) : Exception(message);

internal sealed class SessionCleanupException(
    string message,
    Exception? innerException = null,
    IReadOnlyList<string>? warnings = null)
    : Exception(message, innerException)
{
    internal IReadOnlyList<string> Warnings { get; } = warnings ?? [];
}

internal static class SqlSessionWarningProjection
{
    internal static IReadOnlyList<string> For(Exception exception) =>
        exception is SessionCleanupException cleanup ? cleanup.Warnings : [];
}

internal static class SqlExecution
{
    internal const string IdentitySql =
        "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) AS ServerName,\n" +
        "       DB_NAME() AS DatabaseName;";

    internal static bool TargetMatches(ResolvedTarget expected, string server, string database)
    {
        _ = server; // Server name is informational; endpoint authentication depends on transport mode, and PostgreSQL hostname verification requires sslMode: verify-full.
        return string.Equals(expected.Database, database, StringComparison.Ordinal);
    }
}

internal sealed class SqlClientSessionFactory : ISqlSessionFactory
{
    private static readonly IReadOnlyList<string> AccessTokenArguments =
        ["account", "get-access-token", "--resource", "https://database.windows.net/"];
    private readonly IAzureCli _azureCli;
    private readonly Func<string, string?, CancellationToken, Task<ISqlSession>> _connector;
    private readonly int _connectTimeoutSeconds;

    internal SqlClientSessionFactory(IAzureCli azureCli, int connectTimeoutSeconds = ConnectionConfig.DefaultTimeoutSeconds)
        : this(azureCli, ConnectSqlClientAsync, connectTimeoutSeconds) { }

    internal SqlClientSessionFactory(
        IAzureCli azureCli,
        Func<string, string?, CancellationToken, Task<ISqlSession>> connector,
        int connectTimeoutSeconds = ConnectionConfig.DefaultTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(azureCli);
        ArgumentNullException.ThrowIfNull(connector);
        if (connectTimeoutSeconds is < ConnectionConfig.MinimumTimeoutSeconds or > ConnectionConfig.MaximumTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutSeconds));
        _azureCli = azureCli;
        _connector = connector;
        _connectTimeoutSeconds = connectTimeoutSeconds;
    }

    public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        => await ConnectAsync(target, ct, []);

    public async Task<ISqlSession> ConnectAsync(
        ResolvedTarget target,
        CancellationToken ct,
        List<string> knownSecrets)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(knownSecrets);
        string? accessToken = null;
        if (target.Auth.RequiresAccessToken)
        {
            var recorder = OperationPhaseRecorder.Current;
            var tokenScope = recorder?.CapturesEvents == true
                ? recorder.StartDiagnosticEvent(SqlHarness.Core.Journal.OperationPhase.Connect)
                : default;
            System.Text.Json.JsonElement tokenResponse;
            try
            {
                tokenResponse = await _azureCli.RunJsonAsync(AccessTokenArguments, ct);
                if (recorder?.CapturesEvents == true)
                    tokenScope.Detail("{\"authStrategy\":\"azure-cli\",\"attempt\":1}");
            }
            catch
            {
                tokenScope.Fail(null);
                if (recorder?.CapturesEvents == true)
                    tokenScope.Detail("{\"authStrategy\":\"azure-cli\",\"attempt\":1}");
                throw;
            }
            finally
            {
                tokenScope.Dispose();
            }
            accessToken = ReadAccessToken(tokenResponse);
            knownSecrets.Add(accessToken);
        }

        var connectionString = target.Auth.BuildConnectionString(
            target.Server, target.Database, _connectTimeoutSeconds);
        ISqlSession? session = null;
        try
        {
            session = await _connector(connectionString, accessToken, ct);
            var identity = await ReadIdentityAsync(session, _connectTimeoutSeconds, ct);
            if (!SqlExecution.TargetMatches(target, identity.Server, identity.Database))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");

            session.Identity = new SqlHarnessTargetIdentityReport(
                target.Server, target.Database, identity.Server, identity.Database, target.Mode);
            return session;
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync();
            throw;
        }
    }

    private static string ReadAccessToken(System.Text.Json.JsonElement response)
    {
        if (!response.TryGetProperty("accessToken", out var value) ||
            value.ValueKind != System.Text.Json.JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new AzureCliException("Azure CLI returned no SQL access token.");
        return value.GetString()!;
    }

    private static async Task<(string Server, string Database)> ReadIdentityAsync(
        ISqlSession session, int timeoutSeconds, CancellationToken ct)
    {
        await using var reader = await session.ExecuteReaderAsync(
            new SqlExecutionCommand(SqlExecution.IdentitySql, [], timeoutSeconds), ct);
        if (reader.FieldCount != 2 || !await reader.ReadAsync(ct))
            throw new InvalidOperationException("SQL target identity query returned no identity row.");
        var server = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
        var database = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
        while (await reader.ReadAsync(ct)) { }
        while (await reader.NextResultAsync(ct))
            while (await reader.ReadAsync(ct)) { }
        return (server, database);
    }

    private static async Task<ISqlSession> ConnectSqlClientAsync(
        string connectionString, string? accessToken, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        var opened = false;
        try
        {
            var messages = new SessionMessageBuffer();
            SqlInfoMessageEventHandler handler = (_, args) =>
            {
                foreach (SqlError error in args.Errors)
                    messages.Add(error.Number, error.Message);
            };
            connection.InfoMessage += handler;
            if (accessToken is not null)
                connection.AccessToken = accessToken;
            await connection.OpenAsync(ct);
            opened = true;
            return new SqlClientSession(connection, messages, handler);
        }
        finally
        {
            if (!opened)
                await connection.DisposeAsync();
        }
    }
}

internal sealed class SqlClientSession(
    SqlConnection connection,
    SessionMessageBuffer messages,
    SqlInfoMessageEventHandler infoMessageHandler,
    Func<SqlExecutionCommand, CancellationToken, Task<ISqlReader>>? executeReaderOverride = null,
    Func<string, CancellationToken, Task<bool>>? cleanupOverride = null,
    Action<SqlConnection>? clearPool = null) : ISqlSession
{
    internal const string BatchTimeoutWarning = "batch reached its timeout";
    internal const string ConnectionDiscardedWarning = "connection discarded after failed cleanup";
    private const string CleanupFailureMessage =
        "open transaction could not be rolled back; connection discarded";
    private const string CleanupSql = """
        DECLARE @__sqlharness_global_cursors TABLE (name sysname NOT NULL);
        DECLARE @__sqlharness_cursor_list CURSOR;
        EXEC sys.sp_cursor_list
            @cursor_return = @__sqlharness_cursor_list OUTPUT,
            @cursor_scope = 2;
        DECLARE @__sqlharness_reference_name sysname;
        DECLARE @__sqlharness_cursor_name sysname;
        DECLARE @__sqlharness_cursor_scope smallint;
        DECLARE @__sqlharness_status smallint;
        DECLARE @__sqlharness_model smallint;
        DECLARE @__sqlharness_concurrency smallint;
        DECLARE @__sqlharness_scrollable smallint;
        DECLARE @__sqlharness_open_status smallint;
        DECLARE @__sqlharness_cursor_rows int;
        DECLARE @__sqlharness_fetch_status smallint;
        DECLARE @__sqlharness_column_count smallint;
        DECLARE @__sqlharness_row_count smallint;
        DECLARE @__sqlharness_last_operation smallint;
        DECLARE @__sqlharness_cursor_handle int;
        FETCH NEXT FROM @__sqlharness_cursor_list INTO
            @__sqlharness_reference_name, @__sqlharness_cursor_name, @__sqlharness_cursor_scope,
            @__sqlharness_status, @__sqlharness_model, @__sqlharness_concurrency,
            @__sqlharness_scrollable, @__sqlharness_open_status, @__sqlharness_cursor_rows,
            @__sqlharness_fetch_status, @__sqlharness_column_count, @__sqlharness_row_count,
            @__sqlharness_last_operation, @__sqlharness_cursor_handle;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            INSERT INTO @__sqlharness_global_cursors (name)
            VALUES (@__sqlharness_reference_name);
            FETCH NEXT FROM @__sqlharness_cursor_list INTO
                @__sqlharness_reference_name, @__sqlharness_cursor_name, @__sqlharness_cursor_scope,
                @__sqlharness_status, @__sqlharness_model, @__sqlharness_concurrency,
                @__sqlharness_scrollable, @__sqlharness_open_status, @__sqlharness_cursor_rows,
                @__sqlharness_fetch_status, @__sqlharness_column_count, @__sqlharness_row_count,
                @__sqlharness_last_operation, @__sqlharness_cursor_handle;
        END;
        CLOSE @__sqlharness_cursor_list;
        DEALLOCATE @__sqlharness_cursor_list;
        DECLARE __sqlharness_global_cursor_cleanup CURSOR LOCAL FAST_FORWARD FOR
            SELECT name FROM @__sqlharness_global_cursors;
        OPEN __sqlharness_global_cursor_cleanup;
        FETCH NEXT FROM __sqlharness_global_cursor_cleanup INTO @__sqlharness_cursor_name;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            DECLARE @__sqlharness_cursor_sql nvarchar(512) =
                N'BEGIN TRY CLOSE GLOBAL ' + QUOTENAME(@__sqlharness_cursor_name) +
                N'; END TRY BEGIN CATCH END CATCH; BEGIN TRY DEALLOCATE GLOBAL ' +
                QUOTENAME(@__sqlharness_cursor_name) + N'; END TRY BEGIN CATCH END CATCH;';
            EXEC sys.sp_executesql @__sqlharness_cursor_sql;
            FETCH NEXT FROM __sqlharness_global_cursor_cleanup INTO @__sqlharness_cursor_name;
        END;
        CLOSE __sqlharness_global_cursor_cleanup;
        DEALLOCATE __sqlharness_global_cursor_cleanup;
        DECLARE @__sqlharness_rolled_back bit = 0;
        IF @@TRANCOUNT > 0
        BEGIN
            ROLLBACK TRANSACTION;
            SET @__sqlharness_rolled_back = 1;
        END;
        SELECT @__sqlharness_rolled_back;
        """;
    private bool _discardAfterFailure;
    private Exception? _lastFailure;
    private bool _unusable;
    private bool _poolCleared;

    public IReadOnlyList<SessionMessage> Messages => messages.Snapshot();
    public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;
    public string? ClientConnectionId => connection.ClientConnectionId.ToString();

    public ConsumedSessionMessages ConsumeMessages(int startIndex) => messages.Consume(startIndex);

    public async Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand execution, CancellationToken ct)
    {
        if (_unusable)
            throw new SessionCleanupException(CleanupFailureMessage);
        if (executeReaderOverride is not null)
        {
            try
            {
                return await executeReaderOverride(execution, ct);
            }
            catch (Exception exception)
            {
                _lastFailure = exception;
                if (ShouldDiscard(exception))
                    _discardAfterFailure = true;
                throw;
            }
        }

        var command = connection.CreateCommand();
        var transferred = false;
        try
        {
            BindCommand(command, execution);
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            transferred = true;
            return new SqlClientReader(command, reader, MarkReaderFailure);
        }
        catch (Exception exception)
        {
            _lastFailure = exception;
            if (ShouldDiscard(exception))
                _discardAfterFailure = true;
            throw;
        }
        finally
        {
            if (!transferred)
                await command.DisposeAsync();
        }
    }

    public async Task<IReadOnlyList<string>> EndLogicalBatchAsync(CancellationToken ct)
    {
        if (_unusable)
            throw new SessionCleanupException(CleanupFailureMessage);
        if (_discardAfterFailure || ct.IsCancellationRequested)
            throw DiscardConnection(_lastFailure);

        try
        {
            bool rolledBack;
            if (cleanupOverride is not null)
            {
                rolledBack = await cleanupOverride(CleanupSql, ct);
            }
            else
            {
                await using var command = connection.CreateCommand();
                command.CommandText = CleanupSql;
                command.CommandType = CommandType.Text;
                command.CommandTimeout = 5;
                await using var reader = await command.ExecuteReaderAsync(ct);
                rolledBack = await reader.ReadAsync(ct) && reader.GetBoolean(0);
                while (await reader.NextResultAsync(ct))
                    while (await reader.ReadAsync(ct)) { }
            }

            return rolledBack ? ["open transaction rolled back"] : [];
        }
        catch (Exception exception)
        {
            throw DiscardConnection(exception);
        }
    }

    private SessionCleanupException DiscardConnection(Exception? innerException = null)
    {
        if (!_unusable)
        {
            _unusable = true;
            ClearPool();
        }
        var warnings = IsTimeout(innerException)
            ? new[] { BatchTimeoutWarning, ConnectionDiscardedWarning }
            : [ConnectionDiscardedWarning];
        return new SessionCleanupException(CleanupFailureMessage, innerException, warnings);
    }

    private static bool IsTimeout(Exception? exception) =>
        exception is TimeoutException
        || exception is SqlException sqlException &&
            sqlException.Errors.Cast<SqlError>().Any(error => error.Number == -2);

    private bool ShouldDiscard(Exception exception) =>
        exception is TimeoutException or OperationCanceledException
        || exception is SqlException sqlException &&
            sqlException.Errors.Cast<SqlError>().Any(error => error.Number == -2)
        || connection.State == ConnectionState.Broken;

    private void MarkReaderFailure(Exception exception)
    {
        _lastFailure = exception;
        if (ShouldDiscard(exception))
            _discardAfterFailure = true;
    }

    private void ClearPool()
    {
        if (_poolCleared)
            return;
        _poolCleared = true;
        if (clearPool is null)
            SqlConnection.ClearPool(connection);
        else
            clearPool(connection);
    }

    internal static void BindCommand(SqlCommand command, SqlExecutionCommand execution)
    {
        command.CommandText = execution.Sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = execution.TimeoutSeconds;
        foreach (var parameter in execution.Parameters)
        {
            var sqlParameter = command.Parameters.Add(parameter.Name, parameter.Type);
            if (parameter.Type == SqlDbType.Structured)
            {
                if (parameter.TableTypeName is null || parameter.Rows is null)
                    throw new InvalidOperationException("Structured parameters require a resolved type name and rows.");
                sqlParameter.TypeName = parameter.TableTypeName;
                sqlParameter.Value = parameter.Rows;
                continue;
            }
            if (parameter.UdtTypeName is { } udtTypeName)
                sqlParameter.UdtTypeName = udtTypeName;
            if (parameter.Size is { } size)
                sqlParameter.Size = size;
            if (parameter.Precision is { } precision)
                sqlParameter.Precision = precision;
            if (parameter.Scale is { } scale)
                sqlParameter.Scale = scale;
            sqlParameter.Value = parameter.Value;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_discardAfterFailure || _unusable)
        {
            _unusable = true;
            ClearPool();
        }
        connection.InfoMessage -= infoMessageHandler;
        await connection.DisposeAsync();
    }
}

/// <summary>
/// Maps provider column nullability. <see cref="AllowNull"/> is the long-standing coercion
/// (unknown reads as nullable) used by every public surface; <see cref="Reported"/> keeps
/// "unknown" so result comparison can treat it as unavailable evidence.
/// </summary>
internal static class ProviderNullability
{
    public static bool AllowNull(DbColumn column) => column.AllowDBNull ?? true;

    public static bool? Reported(DbColumn column) => column.AllowDBNull;
}

internal sealed class SqlClientReader : ISqlReader
{
    private readonly SqlCommand _command;
    private readonly SqlDataReader _reader;
    private readonly Action<Exception> _onFailure;
    private IReadOnlyList<DbColumn>? _columns;
    internal SqlClientReader(SqlCommand command, SqlDataReader reader, Action<Exception>? onFailure = null)
    {
        _command = command;
        _reader = reader;
        _onFailure = onFailure ?? (_ => { });
    }
    public int FieldCount => _reader.FieldCount;
    public int RecordsAffected => _reader.RecordsAffected;
    public string GetName(int ordinal) => _reader.GetName(ordinal);
    public Type GetFieldType(int ordinal) => _reader.GetFieldType(ordinal);
    public string GetSqlTypeName(int ordinal) => _reader.GetDataTypeName(ordinal);
    public SqlReaderColumnMetadata GetColumnMetadata(int ordinal)
    {
        var column = Columns[ordinal];
        return new SqlReaderColumnMetadata(
            _reader.GetDataTypeName(ordinal),
            column.ColumnSize,
            column.NumericPrecision is null ? null : checked((byte)column.NumericPrecision.Value),
            column.NumericScale is null ? null : checked((byte)column.NumericScale.Value));
    }
    public bool GetAllowNull(int ordinal) => ProviderNullability.AllowNull(Columns[ordinal]);
    public bool? GetReportedAllowNull(int ordinal) => ProviderNullability.Reported(Columns[ordinal]);
    public object GetValue(int ordinal) => _reader.GetValue(ordinal);
    public async Task<bool> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await _reader.ReadAsync(ct);
        }
        catch (Exception exception)
        {
            _onFailure(exception);
            throw;
        }
    }
    public async Task<bool> NextResultAsync(CancellationToken ct)
    {
        try
        {
            var hasNext = await _reader.NextResultAsync(ct);
            if (hasNext) _columns = null;
            return hasNext;
        }
        catch (Exception exception)
        {
            _onFailure(exception);
            throw;
        }
    }
    private IReadOnlyList<DbColumn> Columns => _columns ??= _reader.GetColumnSchema();
    public async ValueTask DisposeAsync()
    {
        try { await _reader.DisposeAsync(); }
        catch (Exception exception)
        {
            _onFailure(exception);
            throw;
        }
        finally { await _command.DisposeAsync(); }
    }
}
