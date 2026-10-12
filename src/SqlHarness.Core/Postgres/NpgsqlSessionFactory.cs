using System.Data;
using System.Data.Common;
using System.Globalization;

using Npgsql;

using SqlHarness.Core.Targets;

namespace SqlHarness.Core.Postgres;

internal sealed class NpgsqlSessionFactory : ISqlSessionFactory
{
    private const int MaximumConnectTimeoutSeconds = 30;
    private readonly int _connectTimeoutSeconds;

    // Once per process, before the first connection. Npgsql 10 reads date/time as
    // DateOnly/TimeOnly from GetValue; this keeps snapshot and canonical reads on DateTime/TimeSpan.
    // CS0618: GlobalTypeMapper is obsolete but still the documented registration when the app does not use NpgsqlDataSource.
    // NPG9001: AddTypeInfoResolverFactory is the public experimental hook for that resolver.
    static NpgsqlSessionFactory()
    {
#pragma warning disable CS0618, NPG9001
        NpgsqlConnection.GlobalTypeMapper.AddTypeInfoResolverFactory(new LegacyDateAndTimeResolverFactory());
#pragma warning restore CS0618, NPG9001
    }

    internal NpgsqlSessionFactory(int connectTimeoutSeconds = 15)
    {
        if (connectTimeoutSeconds is < 1 or > MaximumConnectTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutSeconds));
        _connectTimeoutSeconds = connectTimeoutSeconds;
    }

    public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connectionString = PostgresConnectionString.Build(target, _connectTimeoutSeconds);
        ISqlSession? session = null;
        try
        {
            var opened = await ConnectNpgsqlAsync(connectionString, ct);
            session = opened.Session;
            var identity = await ReadIdentityAsync(session, _connectTimeoutSeconds, ct);
            var observation = PostgresEndpointIdentity.WithVerifyFullFallback(target, opened.Observation);
            // The established connection is the endpoint. inet_server_addr() stays on the report only.
            if (!PostgresEndpointIdentity.Matches(target, identity.Database, observation, SystemPostgresHostResolver.Instance))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");

            session.Identity = PostgresEndpointIdentity.CreateReport(target, identity.Server, identity.Database);
            return session;
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync();
            throw;
        }
    }

    private static async Task<(string Server, string Database)> ReadIdentityAsync(
        ISqlSession session, int timeoutSeconds, CancellationToken ct)
    {
        await using var reader = await session.ExecuteReaderAsync(
            new SqlExecutionCommand(PostgresPing.IdentitySql, [], timeoutSeconds), ct);
        if (reader.FieldCount != 2 || !await reader.ReadAsync(ct))
            throw new InvalidOperationException("SQL target identity query returned no identity row.");
        var database = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
        var server = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
        while (await reader.ReadAsync(ct)) { }
        while (await reader.NextResultAsync(ct))
            while (await reader.ReadAsync(ct)) { }
        return (server, database);
    }

    private static async Task<(ISqlSession Session, PostgresEndpointObservation Observation)> ConnectNpgsqlAsync(
        string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        var opened = false;
        try
        {
            var messages = new SessionMessageBuffer();
            NoticeEventHandler handler = (_, args) => messages.Add(args.Notice.MessageText);
            connection.Notice += handler;
            await connection.OpenAsync(ct);
            opened = true;
            return (new NpgsqlSession(connection, messages, handler), NpgsqlEndpointObservation.Read(connection));
        }
        finally
        {
            if (!opened)
                await connection.DisposeAsync();
        }
    }
}

internal static class PostgresSessionScope
{
    internal static void BindClassifiedCommand(NpgsqlCommand command, SqlExecutionCommand execution)
    {
        command.CommandText = execution.Sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = execution.TimeoutSeconds;
        PostgresParameters.Bind(command, execution.Parameters);
    }
}

internal sealed class NpgsqlSession(
    NpgsqlConnection connection,
    SessionMessageBuffer messages,
    NoticeEventHandler noticeHandler) : ISqlSession
{
    public IReadOnlyList<string> Messages => messages.Snapshot();
    public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;

    public ConsumedSessionMessages ConsumeMessages(int startIndex) => messages.Consume(startIndex);

    public async Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand execution, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        NpgsqlTransaction? transaction = null;
        try
        {
            BindCommand(command, execution);
            if (execution.PostgresMode is not (PostgresExecutionMode.SessionSetup or PostgresExecutionMode.ApprovedMutation))
            {
                transaction = await connection.BeginTransactionAsync(ct);
                command.Transaction = transaction;
                await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                readOnly.CommandTimeout = execution.TimeoutSeconds;
                await readOnly.ExecuteNonQueryAsync(ct);
            }
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            return new NpgsqlSessionReader(command, reader, transaction);
        }
        catch (Exception exception)
        {
            await NpgsqlSessionReader.CleanupFailedOpenAsync(command, transaction);
            throw PostgresReadOnlyFailure.Map(exception);
        }
    }

    internal static void BindCommand(NpgsqlCommand command, SqlExecutionCommand execution) =>
        PostgresSessionScope.BindClassifiedCommand(command, execution);

    public async ValueTask DisposeAsync()
    {
        connection.Notice -= noticeHandler;
        await connection.DisposeAsync();
    }
}

internal sealed class NpgsqlSessionReader : ISqlReader
{
    private readonly NpgsqlCommand _command;
    private readonly NpgsqlDataReader _reader;
    private readonly NpgsqlTransaction? _transaction;
    private IReadOnlyList<DbColumn>? _columns;
    private bool _currentResultConsumed;
    private bool _allResultsConsumed = true;
    private bool _completed;
    private bool _disposed;
    internal NpgsqlSessionReader(NpgsqlCommand command, NpgsqlDataReader reader, NpgsqlTransaction? transaction)
    {
        _command = command;
        _reader = reader;
        _transaction = transaction;
    }
    public int FieldCount => _reader.FieldCount;
    public int RecordsAffected => _reader.RecordsAffected;
    public string GetName(int ordinal) => _reader.GetName(ordinal);
    public Type GetFieldType(int ordinal) => _reader.GetFieldType(ordinal);
    public bool GetAllowNull(int ordinal) => ProviderNullability.AllowNull(Columns[ordinal]);
    public bool? GetReportedAllowNull(int ordinal) => ProviderNullability.Reported(Columns[ordinal]);
    public object GetValue(int ordinal) => _reader.GetValue(ordinal);
    public async Task<bool> ReadAsync(CancellationToken ct)
    {
        try
        {
            var hasRow = await _reader.ReadAsync(ct);
            if (!hasRow)
                _currentResultConsumed = true;
            return hasRow;
        }
        catch (Exception exception)
        {
            await AbortAsync();
            throw PostgresReadOnlyFailure.Map(exception);
        }
    }
    public async Task<bool> NextResultAsync(CancellationToken ct)
    {
        try
        {
            // A result with no columns has no rows to consume. Advancing before a
            // row-bearing result reaches EOF makes the whole transaction incomplete.
            var priorConsumed = _currentResultConsumed || FieldCount == 0;
            _allResultsConsumed &= priorConsumed;
            var hasNext = await _reader.NextResultAsync(ct);
            if (hasNext)
            {
                _columns = null;
                _currentResultConsumed = false;
            }
            else
            {
                _completed = _allResultsConsumed;
            }
            return hasNext;
        }
        catch (Exception exception)
        {
            await AbortAsync();
            throw PostgresReadOnlyFailure.Map(exception);
        }
    }
    private IReadOnlyList<DbColumn> Columns => _columns ??= _reader.GetColumnSchema();
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        Exception? primary = null;
        try
        {
            await _reader.DisposeAsync();
            if (_transaction is not null)
            {
                if (_completed)
                    await _transaction.CommitAsync();
                else
                    await _transaction.RollbackAsync();
            }
        }
        catch (Exception exception)
        {
            primary = exception;
            if (_transaction is not null)
                await TryRollbackAsync(_transaction);
        }
        finally
        {
            await DisposeResourcesAsync(_reader, _command, _transaction, primary);
        }

        if (primary is not null)
            throw PostgresReadOnlyFailure.Map(primary);
    }

    private async Task AbortAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await CleanupFailedOpenAsync(_reader, _command, _transaction);
    }

    internal static async Task CleanupFailedOpenAsync(NpgsqlCommand command, NpgsqlTransaction? transaction)
    {
        await CleanupFailedOpenAsync(null, command, transaction);
    }

    private static async Task CleanupFailedOpenAsync(NpgsqlDataReader? reader, NpgsqlCommand command, NpgsqlTransaction? transaction)
    {
        if (transaction is not null)
            await TryRollbackAsync(transaction);
        await DisposeResourcesAsync(reader, command, transaction, primary: new InvalidOperationException("Preserve the original database failure."));
    }

    private static async Task TryRollbackAsync(NpgsqlTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch { /* Best-effort cleanup must preserve the SQL failure. */ }
    }

    private static async Task DisposeResourcesAsync(
        NpgsqlDataReader? reader,
        NpgsqlCommand command,
        NpgsqlTransaction? transaction,
        Exception? primary)
    {
        if (reader is not null)
            await IgnoreCleanupAsync(() => reader.DisposeAsync().AsTask(), primary);
        await IgnoreCleanupAsync(() => command.DisposeAsync().AsTask(), primary);
        if (transaction is not null)
            await IgnoreCleanupAsync(() => transaction.DisposeAsync().AsTask(), primary);
    }

    private static async Task IgnoreCleanupAsync(Func<Task> cleanup, Exception? primary)
    {
        try { await cleanup(); }
        catch when (primary is not null) { /* Preserve the primary failure. */ }
    }
}