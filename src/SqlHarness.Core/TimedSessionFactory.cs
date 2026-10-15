using System.Text.Json;

using Microsoft.Data.SqlClient;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Targets;

using JournalOperationPhase = SqlHarness.Core.Journal.OperationPhase;

namespace SqlHarness.Core;

internal sealed class TimedSessionFactory(ISqlSessionFactory inner) : ISqlSessionFactory
{
    private readonly ISqlSessionFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
    {
        var recorder = OperationPhaseRecorder.Current;
        return recorder is null
            ? _inner.ConnectAsync(target, ct)
            : ConnectTrackedAsync(recorder, target, ct);
    }

    public Task<ISqlSession> ConnectAsync(
        ResolvedTarget target,
        CancellationToken ct,
        List<string> knownSecrets)
    {
        var recorder = OperationPhaseRecorder.Current;
        return recorder is null
            ? _inner.ConnectAsync(target, ct, knownSecrets)
            : ConnectTrackedAsync(recorder, target, ct, knownSecrets);
    }

    private async Task<ISqlSession> ConnectTrackedAsync(
        OperationPhaseRecorder recorder,
        ResolvedTarget target,
        CancellationToken ct,
        List<string>? knownSecrets = null)
    {
        var scope = recorder.Start(JournalOperationPhase.Connect);
        try
        {
            var session = knownSecrets is null
                ? await _inner.ConnectAsync(target, ct)
                : await _inner.ConnectAsync(target, ct, knownSecrets);
            if (recorder.CapturesEvents)
            {
                try
                {
                    var detail = new Dictionary<string, object?>
                    {
                        ["clientConnectionId"] = session.ClientConnectionId,
                        ["attempt"] = 1,
                    };
                    var authStrategy = AuthStrategyLabel(target);
                    if (authStrategy is not null)
                        detail["authStrategy"] = authStrategy;
                    scope.Detail(JsonSerializer.Serialize(detail));
                }
                catch
                {
                    // Diagnostic detail is best-effort and cannot fail a connection.
                }
            }

            return session;
        }
        catch (Exception exception)
        {
            int? errorNumber = null;
            try
            {
                if (exception is SqlException sqlException)
                    errorNumber = sqlException.Number;
            }
            catch
            {
                // Reading a provider error number is best-effort diagnostic work.
            }
            scope.Fail(errorNumber);
            throw;
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static string? AuthStrategyLabel(ResolvedTarget target)
    {
        if (target.Engine == SqlEngine.Postgres)
            return "postgres";

        return target.Auth.Strategy switch
        {
            AuthStrategy.AdDefault => "ad-default",
            AuthStrategy.AzureCli => "azure-cli",
            AuthStrategy.Sql => "sql",
            AuthStrategy.Integrated => null,
            _ => null,
        };
    }
}