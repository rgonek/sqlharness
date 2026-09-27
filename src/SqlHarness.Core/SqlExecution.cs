using System.Data;
using System.Data.Common;
using System.Globalization;

using Microsoft.Data.SqlClient;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal sealed record SqlExecutionCommand(
    string Sql,
    IReadOnlyList<SqlHarnessParameter> Parameters,
    int TimeoutSeconds);

internal interface ISqlReader : IAsyncDisposable
{
    int FieldCount { get; }
    int RecordsAffected { get; }
    string GetName(int ordinal);
    Type GetFieldType(int ordinal);
    bool GetAllowNull(int ordinal);
    object GetValue(int ordinal);
    Task<bool> ReadAsync(CancellationToken ct);
    Task<bool> NextResultAsync(CancellationToken ct);
}

internal interface ISqlSession : IAsyncDisposable
{
    IReadOnlyList<string> Messages { get; }
    SqlHarnessTargetIdentityReport Identity { get; set; }
    Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct);

    /// <summary>
    /// Per-command message consumption: returns the messages queued at or after
    /// <paramref name="startIndex"/> so a session never retains notices from all
    /// previous repetitions. Production sessions additionally release the consumed
    /// buffer memory; the default keeps the legacy slice without releasing.
    /// </summary>
    ConsumedSessionMessages ConsumeMessages(int startIndex) =>
        new ConsumedSessionMessages(Messages.Skip(Math.Max(0, startIndex)).ToArray(), 0);
}

/// <summary>
/// One command's share of session messages. Diagnostics beyond the per-command
/// memory bound are truncated; <see cref="OmittedMessageCount"/> reports how many
/// arrivals were dropped since the previous consumption.
/// </summary>
internal sealed record ConsumedSessionMessages(
    IReadOnlyList<string> Messages,
    int OmittedMessageCount);

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
    private readonly List<string> _messages = new();
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

    public void Add(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            _messages.Add(message);
            _retainedChars += message.Length;
            while ((_messages.Count > MaxMessagesPerCommand ||
                _retainedChars > MaxCharactersPerCommand) &&
                _messages.Count > 0)
            {
                // Evict oldest first. A single arrival larger than the whole
                // budget is dropped outright, so the bound always holds.
                string oldest = _messages[0];
                _messages.RemoveAt(0);
                _retainedChars -= oldest.Length;
                _droppedSinceConsume++;
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
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
            IReadOnlyList<string> slice = _messages.Skip(start).ToArray();
            int omitted = _droppedSinceConsume;
            _messages.Clear();
            _retainedChars = 0;
            _droppedSinceConsume = 0;
            return new ConsumedSessionMessages(slice, omitted);
        }
    }
}

internal interface ISqlSessionFactory
{
    Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct);
}

internal sealed class SqlTargetMismatchException(string message) : Exception(message);

internal static class SqlExecution
{
    internal const string IdentitySql =
        "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) AS ServerName,\n" +
        "       DB_NAME() AS DatabaseName;";

    internal static bool TargetMatches(ResolvedTarget expected, string server, string database)
    {
        // SQL Server only. Postgres uses PostgresEndpointIdentity and does not compare inet_server_addr() text.
        // Database identity is always authoritative: Initial Catalog / current_database() must match.
        if (!string.Equals(expected.Database, database, StringComparison.Ordinal))
            return false;

        // Loopback endpoints (Docker-published SQL, local instances) connect by client host:port.
        // SERVERPROPERTY('ServerName') / inet_server_addr() may not return localhost —
        // so only the database name can be verified for those targets.
        if (IsLoopbackEndpoint(expected.Server))
            return true;

        return string.Equals(
            NormalizeServer(expected.Server, expected.Engine),
            NormalizeServer(server, expected.Engine),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the resolved DataSource is a loopback endpoint (optional tcp: prefix and port).
    /// </summary>
    internal static bool IsLoopbackEndpoint(string server)
    {
        var host = HostFromDataSource(server);
        return host is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "." or "(local)";
    }

    private static string NormalizeServer(string server, SqlEngine engine)
    {
        var host = HostFromDataSource(server);
        if (engine == SqlEngine.Postgres)
            return host;

        const string azureSuffix = ".database.windows.net";
        return host.EndsWith(azureSuffix, StringComparison.OrdinalIgnoreCase)
            ? host[..^azureSuffix.Length]
            : host;
    }

    /// <summary>
    /// Extracts the host (or host\instance) from a SqlClient DataSource, stripping tcp: and port.
    /// </summary>
    private static string HostFromDataSource(string server)
    {
        var normalized = server.Trim();
        if (normalized.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..].TrimStart();

        // Port is always after the final comma in host,port or host\instance,port.
        var comma = normalized.LastIndexOf(',');
        if (comma > 0)
            normalized = normalized[..comma];

        return normalized.Trim();
    }
}

internal sealed class SqlClientSessionFactory : ISqlSessionFactory
{
    private const int MaximumConnectTimeoutSeconds = 30;
    private static readonly IReadOnlyList<string> AccessTokenArguments =
        ["account", "get-access-token", "--resource", "https://database.windows.net/"];
    private readonly IAzureCli _azureCli;
    private readonly Func<string, string?, CancellationToken, Task<ISqlSession>> _connector;
    private readonly int _connectTimeoutSeconds;

    internal SqlClientSessionFactory(IAzureCli azureCli, int connectTimeoutSeconds = 15)
        : this(azureCli, ConnectSqlClientAsync, connectTimeoutSeconds) { }

    internal SqlClientSessionFactory(
        IAzureCli azureCli,
        Func<string, string?, CancellationToken, Task<ISqlSession>> connector,
        int connectTimeoutSeconds = 15)
    {
        ArgumentNullException.ThrowIfNull(azureCli);
        ArgumentNullException.ThrowIfNull(connector);
        if (connectTimeoutSeconds is < 1 or > MaximumConnectTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(connectTimeoutSeconds));
        _azureCli = azureCli;
        _connector = connector;
        _connectTimeoutSeconds = connectTimeoutSeconds;
    }

    public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        string? accessToken = null;
        if (target.Auth.RequiresAccessToken)
            accessToken = ReadAccessToken(await _azureCli.RunJsonAsync(AccessTokenArguments, ct));

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

    internal static bool IsLoopbackEndpoint(string server) => SqlExecution.IsLoopbackEndpoint(server);

    private static async Task<ISqlSession> ConnectSqlClientAsync(
        string connectionString, string? accessToken, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        var opened = false;
        try
        {
            var messages = new SessionMessageBuffer();
            SqlInfoMessageEventHandler handler = (_, args) => messages.Add(args.Message);
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
    SqlInfoMessageEventHandler infoMessageHandler) : ISqlSession
{
    public IReadOnlyList<string> Messages => messages.Snapshot();
    public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;

    public ConsumedSessionMessages ConsumeMessages(int startIndex) => messages.Consume(startIndex);

    public async Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand execution, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        var transferred = false;
        try
        {
            BindCommand(command, execution);
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            transferred = true;
            return new SqlClientReader(command, reader);
        }
        finally
        {
            if (!transferred)
                await command.DisposeAsync();
        }
    }

    internal static void BindCommand(SqlCommand command, SqlExecutionCommand execution)
    {
        command.CommandText = execution.Sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = execution.TimeoutSeconds;
        foreach (var parameter in execution.Parameters)
        {
            var sqlParameter = command.Parameters.Add(parameter.Name, parameter.Type);
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
        connection.InfoMessage -= infoMessageHandler;
        await connection.DisposeAsync();
    }
}

internal sealed class SqlClientReader : ISqlReader
{
    private readonly SqlCommand _command;
    private readonly SqlDataReader _reader;
    private IReadOnlyList<DbColumn>? _columns;
    internal SqlClientReader(SqlCommand command, SqlDataReader reader) { _command = command; _reader = reader; }
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