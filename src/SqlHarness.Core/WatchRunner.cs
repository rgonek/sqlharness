using System.Diagnostics;

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

        var phase = OperationPhase.Authentication;
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
            phase = OperationPhase.Sql;

            var sink = new ReportWatchSink(operation.HistoryLimit);
            var outcome = await RunPollLoopAsync(
                session,
                operation,
                parameters,
                knownSecrets,
                () =>
                {
                    raw ??= new CanonicalResultAccumulator();
                    return raw;
                },
                sink,
                budget,
                start,
                ct);

            rawFootprint = raw is null
                ? new OutputFootprint(0, 0)
                : raw.Complete().Footprint;

            var report = new SqlHarnessWatchReport(
                session.Identity,
                outcome.PollCount,
                ElapsedMilliseconds(start),
                outcome.ExitReason,
                sink.Emitted,
                outcome.TotalChangedPolls,
                outcome.TotalChangedPolls - sink.Emitted.Count);
            var exitCode = outcome.ExitReason == WatchExitReason.MaxDuration
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
                    OperationFailureMapper.Map(exception, phase),
                    null,
                    SecretRedactor.Redact(exception, secrets)),
                rawFootprint);
        }
        finally
        {
            raw?.Dispose();
        }
    }

    /// <summary>
    /// Streaming twin of <see cref="ExecuteAsync"/> for <c>watch --output ndjson</c>.
    /// Same engine, budgets, tie rule and stop criteria as the report path, but every changed
    /// result is emitted to <paramref name="stream"/> immediately (flushed per
    /// record) and no poll history is retained: only counters survive.
    /// The stream always ends with exactly one terminal record
    /// (<c>completed</c> on a stop condition, <c>failed</c> otherwise).
    /// A dead transport cannot produce a success: when even the terminal
    /// record cannot be written, the returned outcome is still a failure.
    /// </summary>
    internal async Task<(SqlHarnessOutcome Outcome, OutputFootprint RawFootprint)> ExecuteNdjsonAsync(
        SqlHarnessWatchOperation operation,
        ResolvedTarget target,
        IReadOnlyList<SqlHarnessParameter> parameters,
        IReadOnlyCollection<string> knownSecrets,
        WatchNdjsonWriter stream,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(knownSecrets);
        ArgumentNullException.ThrowIfNull(stream);

        var phase = OperationPhase.Authentication;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;
        var start = _clock.UtcNow;
        // Same single monotonic budget as ExecuteAsync; only the linked token
        // below reaches I/O so the caller's token stays distinguishable.
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
                // Deadline elapsed during connect: the stream never started
                // (started needs the session identity), so the terminal
                // record is a failed-only stream. The exit code still reports
                // the deadline and gain still counts it as a success.
                EmitFailed(stream, start, SqlHarnessExitCode.WatchMaxDuration,
                    new SqlHarnessError(
                        "watch_max_duration",
                        "watch",
                        "Watch reached its maximum duration before a stop condition was met."),
                    knownSecrets);
                return (new SqlHarnessOutcome(SqlHarnessExitCode.WatchMaxDuration, null, null), rawFootprint);
            }
            await using var session = connected;
            phase = OperationPhase.Sql;

            stream.WriteStarted(new { target = session.Identity }, ElapsedMilliseconds(start));

            var sink = new NdjsonWatchSink(stream);
            var outcome = await RunPollLoopAsync(
                session,
                operation,
                parameters,
                knownSecrets,
                () =>
                {
                    raw ??= new CanonicalResultAccumulator();
                    return raw;
                },
                sink,
                budget,
                start,
                ct);

            rawFootprint = raw is null
                ? new OutputFootprint(0, 0)
                : raw.Complete().Footprint;

            EmitCompleted(stream, start, outcome.ExitReason, outcome.PollCount, outcome.TotalChangedPolls);
            var exitCode = outcome.ExitReason == WatchExitReason.MaxDuration
                ? SqlHarnessExitCode.WatchMaxDuration
                : SqlHarnessExitCode.Success;
            return (new SqlHarnessOutcome(exitCode, null, null), rawFootprint);
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var message = SecretRedactor.Redact(exception, secrets);
            EmitFailed(stream, start, exitCode, SqlHarnessError.From(exitCode, message, PhaseName(phase)), knownSecrets);
            return (new SqlHarnessOutcome(exitCode, null, message), rawFootprint);
        }
        finally
        {
            raw?.Dispose();
        }
    }

    /// <summary>
    /// Terminal state of one <see cref="RunPollLoopAsync"/> run.
    /// </summary>
    private sealed record WatchPollOutcome(WatchExitReason ExitReason, int PollCount, int TotalChangedPolls);

    /// <summary>
    /// Receiver for changed poll results emitted by the polling engine. The
    /// engine owns the budget, the tie rule, change detection and the stop
    /// criteria; a sink only handles one changed result at a time.
    /// </summary>
    private interface IWatchPollSink
    {
        void OnChanged(int poll, long elapsedMilliseconds, string resultHash, IReadOnlyList<SqlHarnessResultSetReport> resultSets);
    }

    /// <summary>
    /// Report-path sink: retains at most <c>HistoryLimit</c> changed reports
    /// in memory, always keeping the latest full result. Change detection and
    /// the unchanged tracker observe every poll in the engine, so the stop
    /// criteria never depend on retention.
    /// </summary>
    private sealed class ReportWatchSink(int historyLimit) : IWatchPollSink
    {
        private readonly List<SqlHarnessWatchPoll> _emitted = new();

        public IReadOnlyList<SqlHarnessWatchPoll> Emitted => _emitted;

        public void OnChanged(int poll, long elapsedMilliseconds, string resultHash, IReadOnlyList<SqlHarnessResultSetReport> resultSets)
        {
            _emitted.Add(new SqlHarnessWatchPoll(poll, elapsedMilliseconds, resultHash, resultSets));
            // Evict the oldest report but always keep the latest full result.
            if (_emitted.Count > historyLimit)
                _emitted.RemoveAt(0);
        }
    }

    /// <summary>
    /// NDJSON-path sink: every changed result is emitted to the stream
    /// immediately (flushed per record) and no poll history is retained.
    /// </summary>
    private sealed class NdjsonWatchSink(WatchNdjsonWriter stream) : IWatchPollSink
    {
        public void OnChanged(int poll, long elapsedMilliseconds, string resultHash, IReadOnlyList<SqlHarnessResultSetReport> resultSets) =>
            stream.WriteChanged(
                new
                {
                    poll,
                    resultHash,
                    resultSets,
                },
                elapsedMilliseconds);
    }

    /// <summary>
    /// The single watch polling engine behind <see cref="ExecuteAsync"/> and
    /// <see cref="ExecuteNdjsonAsync"/>, which remain thin adapters. One
    /// monotonic budget covers the whole operation; the engine owns the
    /// per-poll read with the sub-second tie rule, change detection and both
    /// stop criteria, so terminal decisions are identical for the report and
    /// NDJSON paths by construction.
    /// </summary>
    private async Task<WatchPollOutcome> RunPollLoopAsync(
        ISqlSession session,
        SqlHarnessWatchOperation operation,
        IReadOnlyList<SqlHarnessParameter> parameters,
        IReadOnlyCollection<string> knownSecrets,
        Func<CanonicalResultAccumulator> getRaw,
        IWatchPollSink sink,
        IWatchBudget budget,
        DateTimeOffset start,
        CancellationToken ct)
    {
        WatchCondition? condition = operation.Until is null
            ? null
            : WatchCondition.Parse(operation.Until);
        WatchUnchangedTracker? tracker = operation.UntilUnchanged is int required
            ? new WatchUnchangedTracker(required)
            : null;

        string? previousHash = null;
        var poll = 0;
        var totalChangedPolls = 0;

        while (true)
        {
            // Never start a new command after the budget is exhausted: a delay that
            // ends exactly at the deadline exits here instead of polling again.
            if (budget.Remaining <= TimeSpan.Zero)
                return new WatchPollOutcome(WatchExitReason.MaxDuration, poll, totalChangedPolls);

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
                    getRaw,
                    budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Deadline cancelled an in-flight poll: keep the last complete state.
                return new WatchPollOutcome(WatchExitReason.MaxDuration, poll, totalChangedPolls);
            }

            // Tie rule: only a result completed within budget may satisfy the stop
            // condition. A read that overruns the deadline is dropped; both paths
            // keep the last complete in-budget state.
            if (budget.IsExpired)
                return new WatchPollOutcome(WatchExitReason.MaxDuration, poll, totalChangedPolls);

            var hash = collected.Canonical.Hash;
            var elapsed = ElapsedMilliseconds(start);
            if (previousHash is null || !string.Equals(previousHash, hash, StringComparison.Ordinal))
            {
                totalChangedPolls++;
                sink.OnChanged(poll, elapsed, hash, collected.ResultSets);
                previousHash = hash;
            }

            if (condition is not null)
            {
                var firstSet = collected.ResultSets.Count > 0
                    ? collected.ResultSets[0]
                    : throw new SqlHarnessSafetyException("Watch condition requires a result set.");
                if (condition.IsMet(firstSet))
                    return new WatchPollOutcome(WatchExitReason.ConditionMet, poll, totalChangedPolls);
            }

            if (tracker is not null && tracker.Observe(hash))
                return new WatchPollOutcome(WatchExitReason.Unchanged, poll, totalChangedPolls);

            if (budget.Remaining <= TimeSpan.Zero)
                return new WatchPollOutcome(WatchExitReason.MaxDuration, poll, totalChangedPolls);

            // Clamp to the remaining budget so a long --interval cannot overshoot it.
            var delay = operation.Interval < budget.Remaining ? operation.Interval : budget.Remaining;
            try
            {
                await _clock.DelayAsync(delay, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new WatchPollOutcome(WatchExitReason.MaxDuration, poll, totalChangedPolls);
            }
        }
    }

    private void EmitCompleted(
        WatchNdjsonWriter stream, DateTimeOffset start, WatchExitReason reason, int pollCount, int totalChangedPolls)
    {
        // The terminal record must be emitted: a transport failure here is
        // converted to an exception so the outcome reports failure instead of
        // a silent success with no terminal record.
        try
        {
            stream.WriteCompleted(
                new
                {
                    exitReason = WatchNdjsonWriter.FormatExitReason(reason),
                    pollCount,
                    totalChangedPolls,
                    omittedPolls = 0,
                },
                ElapsedMilliseconds(start));
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("The NDJSON watch stream transport failed on the terminal record.", exception);
        }
    }

    private void EmitFailed(
        WatchNdjsonWriter stream,
        DateTimeOffset start,
        SqlHarnessExitCode exitCode,
        SqlHarnessError error,
        IReadOnlyCollection<string> knownSecrets)
    {
        // Best effort: a dead transport cannot take a terminal record, but the
        // returned outcome still reports the failure (never a success).
        try
        {
            var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
            stream.WriteFailed(
                new
                {
                    exitCode = (int)exitCode,
                    error = error with { Message = SecretRedactor.Redact(error.Message, secrets) },
                },
                ElapsedMilliseconds(start));
        }
        catch
        {
            // Swallowed: the outcome below carries the failure.
        }
    }

    private static string PhaseName(OperationPhase phase) => phase switch
    {
        OperationPhase.Validation => "validation",
        OperationPhase.Authentication => "authentication",
        OperationPhase.Sql => "sql",
        OperationPhase.Artifact => "artifact",
        _ => "execution",
    };

    private static int ClampCommandTimeout(int timeoutSeconds, TimeSpan remaining) =>
        Math.Min(timeoutSeconds, Math.Max(1, (int)remaining.TotalSeconds));

    private long ElapsedMilliseconds(DateTimeOffset start)
    {
        var elapsed = _clock.UtcNow - start;
        return elapsed < TimeSpan.Zero ? 0 : (long)elapsed.TotalMilliseconds;
    }
}