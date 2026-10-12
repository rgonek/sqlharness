using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal sealed class EngineSessionFactory : ISqlSessionFactory
{
    private readonly ISqlSessionFactory _sqlServer;
    private readonly ISqlSessionFactory _postgres;

    internal EngineSessionFactory(ISqlSessionFactory sqlServer, ISqlSessionFactory postgres)
    {
        _sqlServer = sqlServer ?? throw new ArgumentNullException(nameof(sqlServer));
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
    }

    public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Engine switch
        {
            SqlEngine.SqlServer => _sqlServer.ConnectAsync(target, ct),
            SqlEngine.Postgres => _postgres.ConnectAsync(target, ct),
            _ => throw new SqlHarnessSafetyException("Unknown engine."),
        };
    }

    public Task<ISqlSession> ConnectAsync(
        ResolvedTarget target,
        CancellationToken ct,
        List<string> knownSecrets)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(knownSecrets);
        return target.Engine switch
        {
            SqlEngine.SqlServer => _sqlServer.ConnectAsync(target, ct, knownSecrets),
            SqlEngine.Postgres => _postgres.ConnectAsync(target, ct, knownSecrets),
            _ => throw new SqlHarnessSafetyException("Unknown engine."),
        };
    }
}
