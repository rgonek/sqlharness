using System.Diagnostics;

namespace SqlHarness.Core;

/// <summary>
/// Records every operation in the activity journal around an inner module.
/// The inner outcome is returned unchanged except that its emission receipt is
/// wrapped to also record token footprints; journal or identity failures are
/// swallowed so they can never change output or exit codes.
/// </summary>
public sealed class JournalingModule : ISqlHarnessModule
{
    private readonly ISqlHarnessModule _inner;
    private readonly Lazy<IActivityJournal?> _journal;
    private readonly Lazy<SessionIdentity?> _session;

    public JournalingModule(ISqlHarnessModule inner, Func<IActivityJournal> journal, Func<SessionIdentity> session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(session);
        _journal = new Lazy<IActivityJournal?>(() => Try(journal), LazyThreadSafetyMode.ExecutionAndPublication);
        _session = new Lazy<SessionIdentity?>(() => Try(session), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
        RunAsync(operation, effective => _inner.ExecuteAsync(effective, ct));

    public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation operation,
        TextWriter writer,
        CancellationToken ct = default) =>
        RunAsync(operation, effective => _inner.ExecuteWatchNdjsonAsync((SqlHarnessWatchOperation)effective, writer, ct));

    private async Task<SqlHarnessOutcome> RunAsync(SqlHarnessOperation operation, Func<SqlHarnessOperation, Task<SqlHarnessOutcome>> run)
    {
        var journal = _journal.Value;
        var handle = Begin(journal, operation);
        var effective = journal is not null && handle is not null && operation is SqlHarnessWatchOperation watch
            ? watch with { Progress = ProgressRecorder(journal, handle) }
            : operation;
        var stopwatch = Stopwatch.StartNew();
        SqlHarnessOutcome outcome;
        try
        {
            outcome = await run(effective);
        }
        catch (OperationCanceledException)
        {
            Complete(journal, handle, () => OperationJournalDescriber.Cancelled(stopwatch.ElapsedMilliseconds));
            throw;
        }
        catch (Exception)
        {
            Complete(journal, handle, () => OperationJournalDescriber.Crashed(stopwatch.ElapsedMilliseconds));
            throw;
        }

        var completed = Complete(journal, handle, () => OperationJournalDescriber.DescribeEnd(outcome, stopwatch.ElapsedMilliseconds));
        if (completed && journal is not null && handle is not null && outcome.BenchmarkRuns is { Count: > 0 } runs)
            RecordBenchmark(journal, handle, runs);

        // A failed completion (busy or broken journal) skips the emission write, so one
        // operation never waits on a locked journal a second time.
        if (!completed || journal is null || handle is null || outcome.EmissionReceipt is not { } inner)
            return outcome;

        var wrapped = new SqlHarnessEmissionReceipt(async (emitted, ct) =>
        {
            var exitCode = await inner.CompleteAsync(emitted, ct);
            try
            {
                journal.RecordEmission(handle, inner.RawFootprint, emitted);
            }
            catch (Exception)
            {
                // IActivityJournal implementations do not throw; this guards third-party implementations.
            }

            return exitCode;
        })
        {
            RawFootprint = inner.RawFootprint,
        };
        return outcome with { EmissionReceipt = wrapped };
    }

    private JournalHandle? Begin(IActivityJournal? journal, SqlHarnessOperation operation)
    {
        // A disabled journal records nothing, so the process tree is not walked at all.
        if (journal is null or NullActivityJournal || _session.Value is not { } session)
            return null;
        try
        {
            return journal.Begin(session, OperationJournalDescriber.DescribeStart(operation));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Complete(IActivityJournal? journal, JournalHandle? handle, Func<OperationEnd> end)
    {
        if (journal is null || handle is null)
            return false;
        try
        {
            // The end row is described inside the guard: a describer failure must neither turn
            // a success into an exception nor replace the operation's own exception.
            return journal.Complete(handle, end());
        }
        catch (Exception)
        {
            // IActivityJournal implementations do not throw; this guards third-party implementations.
            return false;
        }
    }

    private static void RecordBenchmark(IActivityJournal journal, JournalHandle handle, IReadOnlyList<CompareRunArtifact> runs)
    {
        try
        {
            // Plan parsing and aggregation run inside the guard: a malformed run never
            // turns a successful benchmark into an exception.
            journal.RecordBenchmark(handle, JournalBenchmarkBuilder.Build(runs));
        }
        catch (Exception)
        {
            // Best-effort: the journal row stays completed without benchmark detail.
        }
    }

    private static Action<WatchProgress> ProgressRecorder(IActivityJournal journal, JournalHandle handle)
    {
        var enabled = true;
        return progress =>
        {
            if (!enabled)
                return;
            try
            {
                // One failed write (busy or broken journal) stops progress for this watch,
                // so a locked journal cannot add its timeout to every poll.
                enabled = journal.RecordWatchProgress(handle, progress);
            }
            catch (Exception)
            {
                enabled = false;
            }
        };
    }

    private static T? Try<T>(Func<T> factory) where T : class
    {
        try
        {
            return factory();
        }
        catch (Exception)
        {
            return null;
        }
    }
}