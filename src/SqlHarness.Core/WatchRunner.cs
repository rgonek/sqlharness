using Microsoft.Data.SqlClient;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal interface IWatchClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

internal sealed class SystemWatchClock : IWatchClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

internal sealed class WatchRunner(ISqlSessionFactory sessions, IWatchClock clock)
{
    private readonly ISqlSessionFactory _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IWatchClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    internal async Task<(SqlHarnessOutcome Outcome, OutputFootprint RawFootprint)> ExecuteAsync(
        SqlHarnessWatchOperation operation,
        ResolvedTarget target,
        IReadOnlyList<SqlHarnessParameter> parameters,
        IReadOnlyCollection<string> knownSecrets,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(knownSecrets);

        var phase = WatchPhase.Authentication;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;
        var start = _clock.UtcNow;
        var deadline = start + operation.MaxDuration;

        try
        {
            await using var session = await _sessions.ConnectAsync(target, ct);
            phase = WatchPhase.Sql;

            WatchCondition? condition = operation.Until is null
                ? null
                : WatchCondition.Parse(operation.Until);
            WatchUnchangedTracker? tracker = operation.UntilUnchanged is int required
                ? new WatchUnchangedTracker(required)
                : null;

            var emitted = new List<SqlHarnessWatchPoll>();
            string? previousHash = null;
            var poll = 0;
            WatchExitReason exitReason;
            var execution = new SqlExecutionCommand(operation.Sql, parameters, operation.TimeoutSeconds);

            while (true)
            {
                poll++;
                var collected = await QueryResultCollector.CollectAsync(
                    session,
                    execution,
                    operation.MaxRows,
                    knownSecrets,
                    () =>
                    {
                        raw ??= new CanonicalResultAccumulator();
                        return raw;
                    },
                    ct);

                var hash = collected.Canonical.Hash;
                var elapsed = ElapsedMilliseconds(start);
                if (previousHash is null || !string.Equals(previousHash, hash, StringComparison.Ordinal))
                {
                    emitted.Add(new SqlHarnessWatchPoll(
                        poll,
                        elapsed,
                        hash,
                        collected.ResultSets));
                    previousHash = hash;
                }

                if (condition is not null)
                {
                    var firstSet = collected.ResultSets.Count > 0
                        ? collected.ResultSets[0]
                        : throw new SqlHarnessSafetyException("Watch condition requires a result set.");
                    if (condition.IsMet(firstSet))
                    {
                        exitReason = WatchExitReason.ConditionMet;
                        break;
                    }
                }

                if (tracker is not null && tracker.Observe(hash))
                {
                    exitReason = WatchExitReason.Unchanged;
                    break;
                }

                // Deadline is checked before the next delay so a poll that lands on the
                // deadline still reports and then exits without sleeping past max duration.
                if (_clock.UtcNow >= deadline)
                {
                    exitReason = WatchExitReason.MaxDuration;
                    break;
                }

                await _clock.DelayAsync(operation.Interval, ct);
            }

            rawFootprint = raw is null
                ? new OutputFootprint(0, 0)
                : raw.Complete().Footprint;

            var report = new SqlHarnessWatchReport(
                session.Identity,
                poll,
                ElapsedMilliseconds(start),
                exitReason,
                emitted);
            var exitCode = exitReason == WatchExitReason.MaxDuration
                ? SqlHarnessExitCode.WatchMaxDuration
                : SqlHarnessExitCode.Success;
            return (new SqlHarnessOutcome(exitCode, report, null), rawFootprint);
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
            return (
                new SqlHarnessOutcome(
                    MapException(exception, phase),
                    null,
                    SecretRedactor.Redact(exception, secrets)),
                rawFootprint);
        }
        finally
        {
            raw?.Dispose();
        }
    }

    private long ElapsedMilliseconds(DateTimeOffset start)
    {
        var elapsed = _clock.UtcNow - start;
        return elapsed < TimeSpan.Zero ? 0 : (long)elapsed.TotalMilliseconds;
    }

    private static SqlHarnessExitCode MapException(Exception exception, WatchPhase phase) => exception switch
    {
        SqlTargetMismatchException => SqlHarnessExitCode.TargetMismatch,
        SqlHarnessSafetyException => SqlHarnessExitCode.Safety,
        AzureCliException => SqlHarnessExitCode.Authentication,
        SqlException when phase == WatchPhase.Authentication => SqlHarnessExitCode.Authentication,
        SqlException => SqlHarnessExitCode.SqlExecution,
        TimeoutException => SqlHarnessExitCode.SqlExecution,
        OperationCanceledException when phase == WatchPhase.Sql => SqlHarnessExitCode.SqlExecution,
        _ when phase == WatchPhase.Authentication => SqlHarnessExitCode.Authentication,
        _ => SqlHarnessExitCode.SqlExecution,
    };

    private enum WatchPhase
    {
        Authentication,
        Sql,
    }
}