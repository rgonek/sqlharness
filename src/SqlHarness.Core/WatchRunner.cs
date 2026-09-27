using Microsoft.Data.SqlClient;
using System.Diagnostics;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal interface IWatchClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
    IWatchBudget StartBudget(TimeSpan budget, CancellationToken ct);
}

/// <summary>
/// Monotonic budget for one watch operation: the remaining time plus a token
/// linked to the caller's token that cancels when the budget elapses.
/// </summary>
internal interface IWatchBudget : IDisposable
{
    TimeSpan Remaining { get; }
    bool IsExpired { get; }
    CancellationToken Token { get; }
}

internal sealed class SystemWatchClock : IWatchClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public IWatchBudget StartBudget(TimeSpan budget, CancellationToken ct) =>
        new SystemWatchBudget(budget, ct);
}

internal sealed class SystemWatchBudget : IWatchBudget
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TimeSpan _budget;
    private readonly CancellationTokenSource _linked;

    internal SystemWatchBudget(TimeSpan budget, CancellationToken ct)
    {
        _budget = budget;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _linked.CancelAfter(budget);
    }

    public TimeSpan Remaining
    {
        get
        {
            var remaining = _budget - _stopwatch.Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool IsExpired => _stopwatch.Elapsed > _budget;

    public CancellationToken Token => _linked.Token;

    public void Dispose() => _linked.Dispose();
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
        // One monotonic budget covers connect, every poll and every delay. Only the
        // linked token below is passed to I/O; the caller's token stays distinguishable
        // (see the OperationCanceledException filters).
        using var budget = _clock.StartBudget(operation.MaxDuration, ct);

        try
        {
            ISqlSession connected;
            try
            {
                connected = await _sessions.ConnectAsync(target, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Deadline elapsed during connect: no session identity exists, so there
                // is no report to attach - the exit code still reports the deadline.
                return (new SqlHarnessOutcome(SqlHarnessExitCode.WatchMaxDuration, null, null), rawFootprint);
            }
            await using var session = connected;
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

            while (true)
            {
                // Never start a new command after the budget is exhausted: a delay that
                // ends exactly at the deadline exits here instead of polling again.
                if (budget.Remaining <= TimeSpan.Zero)
                {
                    exitReason = WatchExitReason.MaxDuration;
                    break;
                }

                poll++;
                // CommandTimeout is whole seconds, so clamp it down to the remaining
                // budget and let the linked token enforce sub-second precision.
                var execution = new SqlExecutionCommand(
                    operation.Sql,
                    parameters,
                    ClampCommandTimeout(operation.TimeoutSeconds, budget.Remaining));

                CollectedQueryResult collected;
                try
                {
                    collected = await QueryResultCollector.CollectAsync(
                        session,
                        execution,
                        operation.MaxRows,
                        knownSecrets,
                        () =>
                        {
                            raw ??= new CanonicalResultAccumulator();
                            return raw;
                        },
                        budget.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Deadline cancelled an in-flight poll: keep the last complete state.
                    exitReason = WatchExitReason.MaxDuration;
                    break;
                }

                // Tie rule: only a result completed within budget may satisfy the stop
                // condition. A read that overruns the deadline is dropped; the report
                // keeps the last complete in-budget state.
                if (budget.IsExpired)
                {
                    exitReason = WatchExitReason.MaxDuration;
                    break;
                }

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

                if (budget.Remaining <= TimeSpan.Zero)
                {
                    exitReason = WatchExitReason.MaxDuration;
                    break;
                }

                // Clamp to the remaining budget so a long --interval cannot overshoot it.
                var delay = operation.Interval < budget.Remaining ? operation.Interval : budget.Remaining;
                try
                {
                    await _clock.DelayAsync(delay, budget.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    exitReason = WatchExitReason.MaxDuration;
                    break;
                }
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

    private static int ClampCommandTimeout(int timeoutSeconds, TimeSpan remaining) =>
        Math.Min(timeoutSeconds, Math.Max(1, (int)remaining.TotalSeconds));

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