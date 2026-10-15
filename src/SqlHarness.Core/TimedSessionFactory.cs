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
        Dictionary<string, object?>? detail = null;
        if (recorder.CapturesEvents)
        {
            detail = ConnectionDetail(target, clientConnectionId: null);
            SetDetail(ref scope, detail);
        }
        try
        {
            var session = knownSecrets is null
                ? await _inner.ConnectAsync(target, ct)
                : await _inner.ConnectAsync(target, ct, knownSecrets);
            if (recorder.CapturesEvents)
            {
                try
                {
                    detail = ConnectionDetail(target, session.ClientConnectionId);
                    SetDetail(ref scope, detail);
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
            if (recorder.CapturesEvents)
            {
                try
                {
                    if (exception is SqlException sqlException)
                    {
                        errorNumber = sqlException.Number;
                        detail = ConnectionDetail(target, sqlException.ClientConnectionId.ToString());
                    }
                }
                catch
                {
                    // Reading provider diagnostics is best-effort and never masks the connection failure.
                }
                SetDetail(ref scope, detail ?? ConnectionDetail(target, clientConnectionId: null));
            }
            scope.Fail(errorNumber);
            throw;
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static Dictionary<string, object?> ConnectionDetail(ResolvedTarget target, string? clientConnectionId)
    {
        var detail = new Dictionary<string, object?>
        {
            ["clientConnectionId"] = clientConnectionId,
            ["attempt"] = 1,
        };
        var authStrategy = AuthStrategyLabel(target);
        if (authStrategy is not null)
            detail["authStrategy"] = authStrategy;
        return detail;
    }

    private static void SetDetail(ref PhaseScope scope, Dictionary<string, object?> detail)
    {
        try
        {
            scope.Detail(JsonSerializer.Serialize(detail));
        }
        catch
        {
            // Diagnostic detail is best-effort and cannot fail a connection.
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