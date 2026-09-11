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
            session = await ConnectNpgsqlAsync(connectionString, ct);
            var identity = await ReadIdentityAsync(session, _connectTimeoutSeconds, ct);
            if (!SqlExecution.TargetMatches(target, identity.Server, identity.Database))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");

            session.Identity = new SqlHarnessTargetIdentityReport(
                target.Server, target.Database, identity.Server, identity.Database, target.Mode,
                Engine: SqlEngineNames.Postgres);
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

    private static async Task<ISqlSession> ConnectNpgsqlAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        var opened = false;
        try
        {
            var messages = new List<string>();
            NoticeEventHandler handler = (_, args) => messages.Add(args.Notice.MessageText);
            connection.Notice += handler;
            await connection.OpenAsync(ct);
            opened = true;
            return new NpgsqlSession(connection, messages, handler);
        }
        finally
        {
            if (!opened)
                await connection.DisposeAsync();
        }
    }
}

internal sealed class NpgsqlSession(
    NpgsqlConnection connection,
    List<string> messages,
    NoticeEventHandler noticeHandler) : ISqlSession
{
    public IReadOnlyList<string> Messages => messages;
    public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;

    public async Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand execution, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        var transferred = false;
        try
        {
            BindCommand(command, execution);
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            transferred = true;
            return new NpgsqlSessionReader(command, reader);
        }
        finally
        {
            if (!transferred)
                await command.DisposeAsync();
        }
    }

    internal static void BindCommand(NpgsqlCommand command, SqlExecutionCommand execution)
    {
        command.CommandText = execution.Sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = execution.TimeoutSeconds;
        PostgresParameters.Bind(command, execution.Parameters);
    }

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
    private IReadOnlyList<DbColumn>? _columns;
    internal NpgsqlSessionReader(NpgsqlCommand command, NpgsqlDataReader reader)
    {
        _command = command;
        _reader = reader;
    }
    public int FieldCount => _reader.FieldCount;
    public int RecordsAffected => _reader.RecordsAffected;
    public string GetName(int ordinal) => _reader.GetName(ordinal);
    public Type GetFieldType(int ordinal) => _reader.GetFieldType(ordinal);
    public bool GetAllowNull(int ordinal) => Columns[ordinal].AllowDBNull ?? true;
    public object GetValue(int ordinal) => _reader.GetValue(ordinal);
    public Task<bool> ReadAsync(CancellationToken ct) => _reader.ReadAsync(ct);
    public async Task<bool> NextResultAsync(CancellationToken ct)
    {
        var hasNext = await _reader.NextResultAsync(ct);
        if (hasNext) _columns = null;
        return hasNext;
    }
    private IReadOnlyList<DbColumn> Columns => _columns ??= _reader.GetColumnSchema();
    public async ValueTask DisposeAsync()
    {
        try { await _reader.DisposeAsync(); }
        finally { await _command.DisposeAsync(); }
    }
}
